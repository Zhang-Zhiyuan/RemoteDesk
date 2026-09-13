package com.remotedesk.agent;

import android.content.Context;
import android.graphics.SurfaceTexture;
import android.opengl.EGL14;
import android.opengl.EGLConfig;
import android.opengl.EGLContext;
import android.opengl.EGLDisplay;
import android.opengl.EGLSurface;
import android.opengl.GLES11Ext;
import android.opengl.GLES20;
import android.os.Handler;
import android.os.HandlerThread;
import android.view.Surface;
import android.view.TextureView;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.FloatBuffer;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/** Opt-in GPU-only decoder Surface -> spatial upscaler -> viewport.
 * Owns no connection or input geometry. Never reads frames back as Bitmaps.
 * One queued render at most; disabling destroys this entire optional path. */
@android.annotation.SuppressLint("ViewConstructor") // Programmatic only; a live decoder listener is required.
final class AndroidExperimentalUpscaler extends TextureView implements TextureView.SurfaceTextureListener, AutoCloseable {
    interface Listener {
        void surfaceChanged(AndroidExperimentalUpscaler renderer, Surface surface);
        void frameDrawn(AndroidExperimentalUpscaler renderer);
        void failed(AndroidExperimentalUpscaler renderer, String reason);
    }
    private final Listener listener;
    private final HandlerThread thread = new HandlerThread("RemoteDesk-UpscaleGL", android.os.Process.THREAD_PRIORITY_DISPLAY);
    private final Handler handler;
    private final AtomicBoolean closed = new AtomicBoolean();
    private final AtomicBoolean renderQueued = new AtomicBoolean();
    private final AtomicBoolean frameAvailable = new AtomicBoolean();
    private final AtomicInteger generation = new AtomicInteger();
    private final AtomicBoolean failureReported = new AtomicBoolean();
    private final AtomicBoolean firstFrameDrawn = new AtomicBoolean();
    private volatile Surface decoderSurface;
    private volatile int outputWidth, outputHeight;
    private volatile Geometry geometry;
    private final int sourceWidth, sourceHeight;
    // EGL and SurfaceTexture operations below are confined to handler's thread.
    private EGLDisplay display = EGL14.EGL_NO_DISPLAY;
    private EGLContext context = EGL14.EGL_NO_CONTEXT;
    private EGLSurface window = EGL14.EGL_NO_SURFACE;
    private SurfaceTexture decoderTexture;
    private int texture, program;
    private boolean hasFrame;
    private final float[] textureTransform = new float[16];
    private final FloatBuffer vertices = ByteBuffer.allocateDirect(8 * 4).order(ByteOrder.nativeOrder())
        .asFloatBuffer().put(new float[] { -1, -1, 1, -1, -1, 1, 1, 1 });

    AndroidExperimentalUpscaler(Context context, int width, int height, Listener listener) {
        super(context);
        this.listener = listener;
        sourceWidth = Math.max(1, width); sourceHeight = Math.max(1, height);
        geometry = new Geometry(0, 0, sourceWidth, sourceHeight);
        vertices.position(0);
        setOpaque(true); setClickable(false); setFocusable(false);
        setAlpha(0f); // Keep the old frame visible until the GPU draws a new one.
        thread.start(); handler = new Handler(thread.getLooper());
        setSurfaceTextureListener(this);
    }

    Surface decoderSurface() { return decoderSurface; }
    boolean matchesSourceSize(int width, int height) { return sourceWidth == width && sourceHeight == height; }

    void geometry(float left, float top, float width, float height) {
        geometry = new Geometry(left, top, width, height);
        requestRender();
    }

    public void onSurfaceTextureAvailable(SurfaceTexture output, int width, int height) {
        outputWidth = width; outputHeight = height;
        int epoch = generation.incrementAndGet();
        handler.post(() -> {
            if (closed.get() || epoch != generation.get()) return;
            try {
                releaseGl();
                display = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY);
                int[] version = new int[2];
                if (!EGL14.eglInitialize(display, version, 0, version, 1)) throw new IllegalStateException("EGL initialize");
                int[] attributes = { EGL14.EGL_RENDERABLE_TYPE, EGL14.EGL_OPENGL_ES2_BIT,
                    EGL14.EGL_SURFACE_TYPE, EGL14.EGL_WINDOW_BIT, EGL14.EGL_RED_SIZE, 8,
                    EGL14.EGL_GREEN_SIZE, 8, EGL14.EGL_BLUE_SIZE, 8, EGL14.EGL_ALPHA_SIZE, 8, EGL14.EGL_NONE };
                EGLConfig[] configs = new EGLConfig[1]; int[] count = new int[1];
                if (!EGL14.eglChooseConfig(display, attributes, 0, configs, 0, 1, count, 0) || count[0] == 0)
                    throw new IllegalStateException("EGL config");
                context = EGL14.eglCreateContext(display, configs[0], EGL14.EGL_NO_CONTEXT,
                    new int[] { EGL14.EGL_CONTEXT_CLIENT_VERSION, 2, EGL14.EGL_NONE }, 0);
                window = EGL14.eglCreateWindowSurface(display, configs[0], output, new int[] { EGL14.EGL_NONE }, 0);
                if (context == EGL14.EGL_NO_CONTEXT || window == EGL14.EGL_NO_SURFACE ||
                    !EGL14.eglMakeCurrent(display, window, window, context)) throw new IllegalStateException("EGL window");
                EGL14.eglSwapInterval(display, 0);
                int vertex = compile(GLES20.GL_VERTEX_SHADER, VERTEX);
                int fragment = 0;
                try {
                    fragment = compile(GLES20.GL_FRAGMENT_SHADER, FRAGMENT);
                    program = GLES20.glCreateProgram();
                    GLES20.glAttachShader(program, vertex); GLES20.glAttachShader(program, fragment);
                    GLES20.glLinkProgram(program);
                    int[] linked = new int[1]; GLES20.glGetProgramiv(program, GLES20.GL_LINK_STATUS, linked, 0);
                    if (linked[0] == 0) throw new IllegalStateException(GLES20.glGetProgramInfoLog(program));
                } finally {
                    GLES20.glDeleteShader(vertex); if (fragment != 0) GLES20.glDeleteShader(fragment);
                }
                int[] ids = new int[1]; GLES20.glGenTextures(1, ids, 0); texture = ids[0];
                GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, texture);
                GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR);
                GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR);
                GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_CLAMP_TO_EDGE);
                GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE);
                decoderTexture = new SurfaceTexture(texture);
                decoderTexture.setDefaultBufferSize(sourceWidth, sourceHeight);
                decoderTexture.setOnFrameAvailableListener(st -> {
                    if (st != decoderTexture || closed.get() || epoch != generation.get()) return;
                    frameAvailable.set(true); requestRender();
                }, handler);
                decoderSurface = new Surface(decoderTexture);
                post(() -> { if (!closed.get() && epoch == generation.get()) listener.surfaceChanged(this, decoderSurface); });
                postDelayed(() -> {
                    if (!closed.get() && epoch == generation.get() && !firstFrameDrawn.get())
                        fail(new IllegalStateException("GPU upscale first-frame timeout"));
                }, 4000);
            } catch (RuntimeException ex) { fail(ex); }
        });
    }

    public void onSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) {
        outputWidth = width; outputHeight = height; requestRender();
    }

    public boolean onSurfaceTextureDestroyed(SurfaceTexture output) {
        generation.incrementAndGet();
        listener.surfaceChanged(this, null);
        if (!handler.post(() -> { releaseGl(); output.release(); })) output.release();
        return false; // Release only after outstanding EGL work has finished.
    }

    public void onSurfaceTextureUpdated(SurfaceTexture output) { }

    private void requestRender() {
        if (!closed.get() && renderQueued.compareAndSet(false, true)) handler.post(() -> {
            renderQueued.set(false);
            if (closed.get() || decoderTexture == null || outputWidth <= 0 || outputHeight <= 0) return;
            try {
                boolean newFrame = frameAvailable.getAndSet(false);
                if (newFrame) {
                    decoderTexture.updateTexImage(); decoderTexture.getTransformMatrix(textureTransform); hasFrame = true;
                }
                if (!hasFrame) return;
                Geometry g = geometry;
                GLES20.glViewport(0, 0, outputWidth, outputHeight);
                GLES20.glUseProgram(program);
                GLES20.glActiveTexture(GLES20.GL_TEXTURE0);
                GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, texture);
                GLES20.glUniform1i(GLES20.glGetUniformLocation(program, "image"), 0);
                GLES20.glUniform2f(GLES20.glGetUniformLocation(program, "sourceSize"), sourceWidth, sourceHeight);
                GLES20.glUniform1f(GLES20.glGetUniformLocation(program, "outputHeight"), outputHeight);
                GLES20.glUniform4f(GLES20.glGetUniformLocation(program, "rect"), g.left, g.top, g.width, g.height);
                GLES20.glUniformMatrix4fv(GLES20.glGetUniformLocation(program, "transform"), 1, false, textureTransform, 0);
                int position = GLES20.glGetAttribLocation(program, "position");
                GLES20.glEnableVertexAttribArray(position);
                GLES20.glVertexAttribPointer(position, 2, GLES20.GL_FLOAT, false, 0, vertices);
                GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4);
                GLES20.glDisableVertexAttribArray(position);
                if (GLES20.glGetError() != GLES20.GL_NO_ERROR || !EGL14.eglSwapBuffers(display, window))
                    throw new IllegalStateException("GPU upscale draw failed");
                if (newFrame) { firstFrameDrawn.set(true); listener.frameDrawn(this); }
            } catch (RuntimeException ex) { fail(ex); }
        });
    }

    private void fail(RuntimeException ex) {
        if (!closed.get() && failureReported.compareAndSet(false, true))
            post(() -> { if (!closed.get()) listener.failed(this, ex.toString()); });
    }

    public void close() {
        if (!closed.compareAndSet(false, true)) return;
        generation.incrementAndGet();
        handler.post(() -> { releaseGl(); thread.quitSafely(); });
    }

    private void releaseGl() {
        Surface surface = decoderSurface; decoderSurface = null;
        if (surface != null) surface.release();
        if (decoderTexture != null) { decoderTexture.release(); decoderTexture = null; }
        hasFrame = false; frameAvailable.set(false); firstFrameDrawn.set(false);
        if (display != EGL14.EGL_NO_DISPLAY) {
            if (program != 0) GLES20.glDeleteProgram(program);
            if (texture != 0) GLES20.glDeleteTextures(1, new int[] { texture }, 0);
            EGL14.eglMakeCurrent(display, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_CONTEXT);
            if (window != EGL14.EGL_NO_SURFACE) EGL14.eglDestroySurface(display, window);
            if (context != EGL14.EGL_NO_CONTEXT) EGL14.eglDestroyContext(display, context);
            EGL14.eglTerminate(display); EGL14.eglReleaseThread();
        }
        program = texture = 0; display = EGL14.EGL_NO_DISPLAY;
        context = EGL14.EGL_NO_CONTEXT; window = EGL14.EGL_NO_SURFACE;
    }

    private static int compile(int kind, String source) {
        int shader = GLES20.glCreateShader(kind);
        GLES20.glShaderSource(shader, source); GLES20.glCompileShader(shader);
        int[] result = new int[1]; GLES20.glGetShaderiv(shader, GLES20.GL_COMPILE_STATUS, result, 0);
        if (result[0] == 0) {
            String reason = GLES20.glGetShaderInfoLog(shader); GLES20.glDeleteShader(shader);
            throw new IllegalStateException(reason);
        }
        return shader;
    }

    private static final class Geometry {
        final float left, top, width, height;
        Geometry(float l, float t, float w, float h) { left=l; top=t; width=w; height=h; }
    }
    private static final String VERTEX = "attribute vec2 position; void main() { gl_Position = vec4(position,0.,1.); }";
    static final String FRAGMENT = """
        #extension GL_OES_EGL_image_external : require
        precision highp float;
        uniform samplerExternalOES image;
        uniform vec2 sourceSize;
        uniform float outputHeight;
        uniform vec4 rect;
        uniform mat4 transform;
        vec3 readPixel(vec2 uv) {
            uv = clamp(uv, 0.5/sourceSize, 1.0-0.5/sourceSize);
            return texture2D(image, (transform * vec4(uv.x, 1.0-uv.y, 0.0, 1.0)).xy).rgb;
        }
        vec4 weights(float t) {
            float t2=t*t, t3=t2*t;
            return vec4(-0.5*t+t2-0.5*t3, 1.0-2.5*t2+1.5*t3,
                        0.5*t+2.0*t2-1.5*t3, -0.5*t2+0.5*t3);
        }
        void main() {
            vec2 pixel = vec2(gl_FragCoord.x, outputHeight-gl_FragCoord.y);
            vec2 uv = (pixel-rect.xy)/max(rect.zw,vec2(1.0));
            if (any(lessThan(uv,vec2(0.0))) || any(greaterThanEqual(uv,vec2(1.0)))) {
                gl_FragColor=vec4(0.008,0.024,0.09,1.0); return;
            }
            if (all(lessThanEqual(rect.zw,sourceSize))) { gl_FragColor=vec4(readPixel(uv),1.0); return; }
            vec2 p=uv*sourceSize-0.5, base=floor(p);
            vec4 wx=weights(fract(p.x)), wy=weights(fract(p.y));
            vec3 color=vec3(0.0), lo=vec3(1.0), hi=vec3(0.0);
            for (int y=0; y<4; y++) {
                for (int x=0; x<4; x++) {
                    vec3 tap=readPixel((base+vec2(float(x)-0.5,float(y)-0.5))/sourceSize);
                    color+=tap*wx[x]*wy[y];
                    if (x>=1 && x<=2 && y>=1 && y<=2) { lo=min(lo,tap); hi=max(hi,tap); }
                }
            }
            gl_FragColor=vec4(clamp(color,lo,hi),1.0);
        }
        """;
}
