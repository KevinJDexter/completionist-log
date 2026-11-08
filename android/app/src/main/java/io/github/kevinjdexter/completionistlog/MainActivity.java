package io.github.kevinjdexter.completionistlog;

import android.os.Bundle;
import com.getcapacitor.BridgeActivity;

public class MainActivity extends BridgeActivity {
    @Override
    public void onCreate(Bundle savedInstanceState) {
        registerPlugin(LodestonePlugin.class);
        super.onCreate(savedInstanceState);
    }
}
