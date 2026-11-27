package org.sonicroute.fixture.route;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;
import android.widget.Button;
import android.widget.LinearLayout;

public final class RouteSample extends Activity {
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        Button start = new Button(this);
        start.setText("Start silent MEDIA track · 90 seconds");
        start.setOnClickListener(v -> startForegroundService(new Intent(this, SampleTrack.class)));
        Button stop = new Button(this);
        stop.setText("Stop sample");
        stop.setOnClickListener(v -> stopService(new Intent(this, SampleTrack.class)));
        layout.addView(start);
        layout.addView(stop);
        setContentView(layout);
    }
}
