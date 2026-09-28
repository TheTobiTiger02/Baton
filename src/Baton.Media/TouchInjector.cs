using System.Runtime.InteropServices;

namespace Baton.Media;

/// <summary>
/// Real Windows touch input (InjectTouchInput), up to ten fingers. Apps receive genuine touch: they
/// scroll, pinch-zoom and pan on their own, and Windows turns press-and-hold into a right click.
/// Windows cancels a contact that goes quiet, so held fingers are refreshed on a timer.
/// </summary>
public sealed class TouchInjector : IDisposable
{
    private const int MaxContacts = 10;
    private const uint PtTouch = 2;
    private const uint FlagInRange = 0x2, FlagInContact = 0x4, FlagDown = 0x10000, FlagUpdate = 0x20000, FlagUp = 0x40000, FlagCanceled = 0x8000;

    private static int _initialized;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Contact> _contacts = [];
    private readonly Timer _keepAlive;
    private DateTime _lastInjection = DateTime.MinValue;

    public TouchInjector() => _keepAlive = new Timer(_ => KeepAlive(), null, 100, 100);

    public bool IsTouching
    {
        get
        {
            lock (_gate)
            {
                return _contacts.Count > 0;
            }
        }
    }

    /// <summary>Moves one finger to screen pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Touch(TouchAction action, ulong pointerId, int x, int y)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            InitializeTouchInjection(MaxContacts, 3 /* TOUCH_FEEDBACK_NONE */);
        }

        lock (_gate)
        {
            switch (action)
            {
                case TouchAction.Down:
                    if (_contacts.Count >= MaxContacts || _contacts.ContainsKey(pointerId))
                    {
                        return;
                    }

                    _contacts[pointerId] = new Contact(FreeId(), x, y, FlagDown | FlagInRange | FlagInContact);
                    break;
                case TouchAction.Move:
                    if (!_contacts.TryGetValue(pointerId, out var moving))
                    {
                        return;
                    }

                    _contacts[pointerId] = moving with { X = x, Y = y, Flags = FlagUpdate | FlagInRange | FlagInContact };
                    break;
                case TouchAction.Up or TouchAction.Cancel:
                    if (!_contacts.TryGetValue(pointerId, out var lifting))
                    {
                        return;
                    }

                    _contacts[pointerId] = lifting with { X = x, Y = y, Flags = action == TouchAction.Up ? FlagUp : FlagUp | FlagCanceled };
                    break;
            }

            InjectAll();
            foreach (var ended in _contacts.Where(pair => (pair.Value.Flags & FlagUp) != 0).Select(pair => pair.Key).ToArray())
            {
                _contacts.Remove(ended);
            }

            // Every finger still down is an update in the next frame.
            foreach (var key in _contacts.Keys.ToArray())
            {
                _contacts[key] = _contacts[key] with { Flags = FlagUpdate | FlagInRange | FlagInContact };
            }
        }
    }

    /// <summary>Lifts every finger, e.g. when the viewer disconnects mid-gesture.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            if (_contacts.Count == 0)
            {
                return;
            }

            foreach (var key in _contacts.Keys.ToArray())
            {
                _contacts[key] = _contacts[key] with { Flags = FlagUp | FlagCanceled };
            }

            InjectAll();
            _contacts.Clear();
        }
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        ReleaseAll();
    }

    private void KeepAlive()
    {
        lock (_gate)
        {
            if (_contacts.Count > 0 && DateTime.UtcNow - _lastInjection > TimeSpan.FromMilliseconds(90))
            {
                InjectAll();
            }
        }
    }

    private uint FreeId()
    {
        for (uint id = 0; id < MaxContacts; id++)
        {
            if (_contacts.Values.All(contact => contact.Id != id))
            {
                return id;
            }
        }

        return 0;
    }

    private void InjectAll()
    {
        var frame = _contacts.Values.Select(contact => new PointerTouchInfo
        {
            PointerInfo = new PointerInfo
            {
                PointerType = PtTouch,
                PointerId = contact.Id,
                PointerFlags = contact.Flags,
                PixelLocation = new Point { X = contact.X, Y = contact.Y }
            },
            Orientation = 90,
            Pressure = 32000,
            TouchMask = 0x6, // orientation and pressure
            ContactArea = new Rect { Left = contact.X - 2, Top = contact.Y - 2, Right = contact.X + 2, Bottom = contact.Y + 2 }
        }).ToArray();
        if (frame.Length > 0)
        {
            InjectTouchInput((uint)frame.Length, frame);
            _lastInjection = DateTime.UtcNow;
        }
    }

    private sealed record Contact(uint Id, int X, int Y, uint Flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public uint PointerType;
        public uint PointerId;
        public uint FrameId;
        public uint PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr HwndTarget;
        public Point PixelLocation;
        public Point HimetricLocation;
        public Point PixelLocationRaw;
        public Point HimetricLocationRaw;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerTouchInfo
    {
        public PointerInfo PointerInfo;
        public uint TouchFlags;
        public uint TouchMask;
        public Rect ContactArea;
        public Rect ContactAreaRaw;
        public uint Orientation;
        public uint Pressure;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint feedbackMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, [In] PointerTouchInfo[] contacts);
}

/// <summary>Android key codes and meta state, as the phone sends them, to Windows virtual keys.</summary>
public static class KeyMapWindows
{
    public const int MetaShift = 0x1, MetaAlt = 0x2, MetaCtrl = 0x1000, MetaMeta = 0x10000;

    private static readonly Dictionary<int, ushort> Keys = BuildKeys();

    /// <summary>The Windows virtual key for an Android key code, or 0 when there is none.</summary>
    public static ushort VirtualKey(int androidKeyCode) => Keys.GetValueOrDefault(androidKeyCode);

    /// <summary>The modifier keys held in an Android meta state, as Windows virtual keys.</summary>
    public static IReadOnlyList<ushort> Modifiers(int metaState)
    {
        var keys = new List<ushort>();
        if ((metaState & MetaCtrl) != 0)
        {
            keys.Add(0x11);
        }

        if ((metaState & MetaAlt) != 0)
        {
            keys.Add(0x12);
        }

        if ((metaState & MetaShift) != 0)
        {
            keys.Add(0x10);
        }

        if ((metaState & MetaMeta) != 0)
        {
            keys.Add(0x5B);
        }

        return keys;
    }

    private static Dictionary<int, ushort> BuildKeys()
    {
        var keys = new Dictionary<int, ushort>
        {
            [66] = 0x0D, // ENTER
            [160] = 0x0D, // NUMPAD_ENTER
            [67] = 0x08, // DEL (backspace)
            [112] = 0x2E, // FORWARD_DEL
            [61] = 0x09, // TAB
            [62] = 0x20, // SPACE
            [111] = 0x1B, // ESCAPE
            [19] = 0x26, [20] = 0x28, [21] = 0x25, [22] = 0x27, // DPAD up, down, left, right
            [122] = 0x24, [123] = 0x23, // MOVE_HOME, MOVE_END
            [92] = 0x21, [93] = 0x22, // PAGE_UP, PAGE_DOWN
            [124] = 0x2D, // INSERT
            [113] = 0x11, [114] = 0x11, // CTRL
            [57] = 0x12, [58] = 0x12, // ALT
            [59] = 0x10, [60] = 0x10, // SHIFT
            [117] = 0x5B, [118] = 0x5C, // META (Windows key)
            [55] = 0xBC, [56] = 0xBE, [69] = 0xBD, [70] = 0xBB, // , . - =
            [71] = 0xDB, [72] = 0xDD, [73] = 0xDC, [74] = 0xBA, // [ ] \ ;
            [75] = 0xDE, [76] = 0xBF, [68] = 0xC0, // ' / `
            [85] = 0xB3, [126] = 0xB3, [127] = 0xB3, // media play/pause
            [87] = 0xB0, [88] = 0xB1, // media next, previous
            [24] = 0xAF, [25] = 0xAE, [164] = 0xAD, // volume up, down, mute
            [120] = 0x2C // SYSRQ (print screen)
        };

        for (var index = 0; index < 26; index++)
        {
            keys[29 + index] = (ushort)(0x41 + index); // A..Z
        }

        for (var index = 0; index < 10; index++)
        {
            keys[7 + index] = (ushort)(0x30 + index); // 0..9
        }

        for (var index = 0; index < 12; index++)
        {
            keys[131 + index] = (ushort)(0x70 + index); // F1..F12
        }

        return keys;
    }
}
