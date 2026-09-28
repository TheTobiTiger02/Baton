package dev.baton.android.mirror;

// Runs as the shell user through Shizuku (ShizukuShell). Transaction codes are fixed so the
// two sides of an update never disagree.
interface IShizukuShell {
    // Shizuku calls this code to end the service.
    void destroy() = 16777114;

    // Runs a command and returns what it printed.
    String exec(in String[] command) = 1;

    // One touch event with every pointer that is down: action as in MotionEvent.
    boolean injectTouch(int action, long downTime, in int[] ids, in float[] xs, in float[] ys) = 2;

    // One mouse wheel event at (x, y): +1 vertical is wheel up, +1 horizontal is right.
    boolean injectScroll(float x, float y, float horizontal, float vertical) = 3;

    // One key event, as in KeyEvent.
    boolean injectKey(int action, int keyCode, int metaState) = 4;

    // The clipboard's text when it changed since the last call, else null (also for sensitive clips).
    String clipboardText() = 5;
}
