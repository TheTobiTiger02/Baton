package dev.baton.android.mirror;

// The app's side of IShizukuShell.watchSwipes: a three-finger move, as fractions of the screen's
// natural width and height (see ThreeFingerSwipe).
oneway interface ISwipeListener {
    void onSwipe(float dx, float dy);
}
