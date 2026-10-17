package com.remotedesk.agent;

import android.content.Context;
import android.view.View;
import android.widget.LinearLayout;

/** Resolve compact branding before measuring, so hidden copy occupies no space. */
final class AndroidMainHeaderLayout extends LinearLayout {
    private View mark;
    private View subtitle;

    AndroidMainHeaderLayout(Context context) { super(context); }

    void setCompactContent(View mark, View subtitle) {
        this.mark = mark;
        this.subtitle = subtitle;
        requestLayout();
    }

    @Override protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
        if (mark != null && subtitle != null) {
            int widthDp = Math.round(MeasureSpec.getSize(widthMeasureSpec) /
                getResources().getDisplayMetrics().density);
            boolean compact = AndroidAdaptiveLayout.compactMainHeader(widthDp,
                getResources().getConfiguration().fontScale);
            mark.setVisibility(compact ? GONE : VISIBLE);
            subtitle.setVisibility(compact ? GONE : VISIBLE);
        }
        super.onMeasure(widthMeasureSpec, heightMeasureSpec);
    }
}
