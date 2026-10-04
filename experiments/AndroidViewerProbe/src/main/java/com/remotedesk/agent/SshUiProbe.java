package com.remotedesk.agent;

import android.app.AlertDialog;
import android.content.Intent;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.webkit.WebView;
import android.widget.EditText;
import android.widget.TextView;
import java.io.File;
import java.nio.file.Files;
import org.json.JSONObject;

final class SshUiProbe {
    static JSONObject fixture;
    static AndroidSshTerminalActivity activity;
    private static final Handler UI = new Handler(Looper.getMainLooper());
    private static String action = "launch", failure = "";
    private static String clipboardText = "";
    static void created(AndroidSshTerminalActivity owner) {
        activity = owner;
        // Synthetic fixture screenshot only; production keeps FLAG_SECURE.
        UI.post(() -> owner.getWindow().clearFlags(android.view.WindowManager.LayoutParams.FLAG_SECURE));
        UI.postDelayed(SshUiProbe::snapshot, 400);
    }
    static void command(Intent command) throws Exception {
        if (activity == null) return;
        action = command.getStringExtra("action"); failure = "";
        if ("connectSelected".equals(action)) {
            ((EditText) field("username")).setText(fixture.getString("sshUsername"));
            ((View) field("connect")).performClick();
        } else if ("selectKey".equals(action)) {
            android.net.Uri uri=android.provider.DocumentsContract.buildDocumentUri("com.remotedesk.viewerprobe.saf",command.getStringExtra("text"));
            activity.onActivityResult(7101, android.app.Activity.RESULT_OK, new Intent().setData(uri));
        } else if ("connect".equals(action)) {
            ((EditText) field("username")).setText(fixture.getString("sshUsername"));
            java.lang.reflect.Field selected=AndroidSshTerminalActivity.class.getDeclaredField("selectedPrivateKey"); selected.setAccessible(true);
            selected.set(activity, Files.readAllBytes(new File(activity.getFilesDir(),"ssh-key.pem").toPath()));
            ((View) field("connect")).performClick();
        } else if ("trust".equals(action) || "reject".equals(action)) {
            AlertDialog dialog=(AlertDialog)field("trustDialog");
            if (dialog==null) throw new IllegalStateException("Expected real product trust dialog");
            dialog.getButton("trust".equals(action)?AlertDialog.BUTTON_POSITIVE:AlertDialog.BUTTON_NEGATIVE).performClick();
        } else if ("input".equals(action)) {
            int generation=(int)field("inputGeneration");
            ((WebView)field("terminal")).evaluateJavascript("RemoteSsh.input("+JSONObject.quote(command.getStringExtra("text"))+","+generation+")",null);
        } else if ("key".equals(action)) {
            String wanted=command.getStringExtra("text");
            if(!click((View)field("root"),wanted)) throw new IllegalStateException("Missing terminal control: "+wanted);
        } else if ("ime".equals(action)) {
            WebView view=(WebView)field("terminal"); view.requestFocus();
            android.view.inputmethod.InputConnection input=view.onCreateInputConnection(new android.view.inputmethod.EditorInfo());
            if(input==null)throw new IllegalStateException("Terminal IME unavailable");
            input.commitText(command.getStringExtra("text"),1);
        } else if ("clipboard".equals(action)) {
            ((android.content.ClipboardManager)activity.getSystemService(android.content.Context.CLIPBOARD_SERVICE))
                .setPrimaryClip(android.content.ClipData.newPlainText("owned SSH fixture",command.getStringExtra("text")));
        } else if ("clipboardRead".equals(action)) {
            android.content.ClipData clip=((android.content.ClipboardManager)activity.getSystemService(android.content.Context.CLIPBOARD_SERVICE)).getPrimaryClip();
            clipboardText=clip==null?"":String.valueOf(clip.getItemAt(0).getText());
        } else if ("algorithms".equals(action)) {
            JSONObject result=new JSONObject();
            for(String algorithm:new String[]{"SHA256withRSA","SHA256withECDSA","Ed25519","Ed448"}) {
                try {java.security.Signature.getInstance(algorithm);result.put(algorithm,true);}
                catch(java.security.GeneralSecurityException ex){result.put(algorithm,false);}
            }
            for(String name:new String[]{"signature.rsa","signature.ecdsa","signature.ed25519","ssh-ed25519"}) {
                String type=com.jcraft.jsch.JSch.getConfig(name);
                if(type==null)continue;
                JSONObject value=new JSONObject().put("implementation",type);
                try {Object signature=Class.forName(type).getDeclaredConstructor().newInstance();signature.getClass().getMethod("init").invoke(signature);value.put("available",true);}
                catch(Throwable ex){value.put("available",false).put("failureType",ex.getClass().getName());}
                result.put(name,value);
            }
            Files.write(new File(activity.getFilesDir(),"ssh-algorithms.json").toPath(),result.toString(2).getBytes(java.nio.charset.StandardCharsets.UTF_8));
        } else if ("js".equals(action)) {
            ((WebView)field("terminal")).evaluateJavascript(command.getStringExtra("text"),null);
        } else if ("stopRenderer".equals(action)) {
            if (!((WebView)field("terminal")).getWebViewRenderProcess().terminate())
                throw new IllegalStateException("Owned WebView renderer termination rejected");
        } else if ("changeTrust".equals(action)) {
            AndroidRelay.Options target=(AndroidRelay.Options)field("target");
            activity.getSharedPreferences("ssh_target_host_keys",0).edit().putString(AndroidSshProtocol.alias(target), "SHA256:owned-changed-key-negative-control").commit();
        } else if ("clearTrust".equals(action)) {
            activity.getSharedPreferences("ssh_target_host_keys",0).edit().clear().commit();
        } else if ("finish".equals(action)) activity.finish();
    }
    private static Object field(String name) throws Exception { return ViewerProbeApplication.field(activity,name); }
    private static boolean click(View view,String text) {
        if(view instanceof android.widget.Button button && text.contentEquals(button.getText())) {button.performClick();return true;}
        if(view instanceof android.view.ViewGroup group) for(int i=0;i<group.getChildCount();i++)if(click(group.getChildAt(i),text))return true;
        return false;
    }
    private static void snapshot() {
        AndroidSshTerminalActivity owner=activity;
        if (owner==null || owner.isDestroyed()) return;
        try {
            JSONObject data=new JSONObject().put("action",action).put("failure",failure)
                .put("status",((TextView)field("status")).getText()).put("pageReady",field("pageReady"))
                .put("connection",field("connection")!=null).put("trustDialog",field("trustDialog")!=null)
                .put("outputSequence",field("outputSequence")).put("inputGeneration",field("inputGeneration"))
                .put("passwordLength",((EditText)field("password")).length())
                .put("connectEnabled",((View)field("connect")).isEnabled())
                .put("privateKeySelected",field("selectedPrivateKey")!=null)
                .put("passwordHint",((EditText)field("password")).getHint())
                .put("selectionGeneration",field("selectionGeneration"))
                .put("ctrl",((TextView)field("ctrl")).getText())
                .put("clipboardText",clipboardText)
                .put("terminalBounds",ViewerProbeApplication.bounds((View)field("terminal")))
                .put("webView",android.webkit.WebView.getCurrentWebViewPackage().versionName)
                .put("config",owner.getResources().getConfiguration().toString());
            Files.write(new File(owner.getFilesDir(),"ssh-state.json").toPath(),data.toString(2).getBytes(java.nio.charset.StandardCharsets.UTF_8));
            WebView terminal=(WebView)field("terminal");
            terminal.evaluateJavascript("document.querySelector('.xterm-rows') ? document.querySelector('.xterm-rows').textContent.slice(-16000) : (document.body ? document.body.innerText : '')", value -> {
                try { data.put("terminalDom",value); Files.write(new File(owner.getFilesDir(),"ssh-state.json").toPath(),data.toString(2).getBytes(java.nio.charset.StandardCharsets.UTF_8)); }
                catch(Exception ex) { failure="snapshot write failed"; }
            });
        } catch(Exception ex) { failure=ex.toString(); }
        UI.postDelayed(SshUiProbe::snapshot,400);
    }
}
