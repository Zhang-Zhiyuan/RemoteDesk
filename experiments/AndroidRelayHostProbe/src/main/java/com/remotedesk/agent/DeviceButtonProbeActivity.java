package com.remotedesk.agent;

/** Real native measurements for wrapped relay rows; no credentials or network. */
public final class DeviceButtonProbeActivity extends android.app.Activity {
    private final org.json.JSONArray results = new org.json.JSONArray();
    private int index;
    @Override public void onCreate(android.os.Bundle state) { super.onCreate(state); runCase(); }
    private void runCase() {
        if (index == 9) { save(); return; }
        int width = new int[]{240,320,480}[index % 3];
        float scale = new float[]{1f,1.5f,2f}[index / 3];
        android.content.res.Configuration config = new android.content.res.Configuration(getResources().getConfiguration());
        config.fontScale = scale;
        android.content.Context context = createConfigurationContext(config);
        android.widget.Button button = new android.widget.Button(context);
        AndroidUiTheme.styleButton(context, button, AndroidUiTheme.ButtonRole.SECONDARY);
        AndroidUiTheme.styleDeviceButton(context, button);
        button.setText("RemoteDesk Ubuntu 测试机器 · Linux\n在线 · 点击中转连接\n192.0.2.112:56565 / 198.51.100.123:56565 / 203.0.113.111:56565");
        int px = Math.round(width * context.getResources().getDisplayMetrics().density);
        button.measure(android.view.View.MeasureSpec.makeMeasureSpec(px, android.view.View.MeasureSpec.EXACTLY),
            android.view.View.MeasureSpec.makeMeasureSpec(0, android.view.View.MeasureSpec.UNSPECIFIED));
        button.layout(0,0,px,button.getMeasuredHeight());
        android.text.Layout text = button.getLayout();
        boolean pass = text != null && text.getHeight() <= button.getMeasuredHeight()-button.getCompoundPaddingTop()-button.getCompoundPaddingBottom()
            && button.getPaddingTop() >= AndroidDisplay.dp(context, 12) && text.getEllipsisCount(text.getLineCount()-1)==0;
        try { results.put(new org.json.JSONObject().put("widthDp",width).put("fontScale",scale).put("lines",text.getLineCount())
            .put("height",button.getMeasuredHeight()).put("passed",pass)); }
        catch (org.json.JSONException error) { throw new IllegalStateException(error); }
        setContentView(button); index++; button.postDelayed(this::runCase,80);
    }
    private void save() {
        try {
            boolean passed = true; for(int i=0;i<results.length();i++) passed &= results.getJSONObject(i).getBoolean("passed");
            org.json.JSONObject report = new org.json.JSONObject().put("passed",passed).put("cases",results);
            java.nio.file.Files.write(new java.io.File(getFilesDir(),"device-button-probe.json").toPath(),report.toString(2).getBytes(java.nio.charset.StandardCharsets.UTF_8));
        } catch (Exception error) { throw new IllegalStateException(error); }
    }
}
