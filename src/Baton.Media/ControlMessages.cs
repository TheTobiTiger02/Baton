using System.Buffers.Binary;
using System.Text;

namespace Baton.Media;

public enum ControlMessageType : byte
{
    Touch = 0x01,
    Scroll = 0x02,
    Key = 0x03,
    Text = 0x04,
    Navigate = 0x05,
    KeyframeRequest = 0x08,
    Rotate = 0x09,
    MouseMove = 0x0B,
    MouseButton = 0x0C
}

public enum TouchAction : byte
{
    Down = 0,
    Up = 1,
    Move = 2,
    Cancel = 3
}

public enum KeyAction : byte
{
    Down = 0,
    Up = 1
}

public enum NavigationTarget : byte
{
    Back = 0,
    Home = 1,
    Recents = 2,
    Power = 3,
    Notifications = 4
}

public enum MouseButton : byte
{
    Left = 1,
    Right = 2,
    Middle = 3
}

public abstract record ControlMessage;

/// <summary>A touch contact at (<see cref="X"/>, <see cref="Y"/>) in a frame of <see cref="FrameWidth"/> x <see cref="FrameHeight"/>.</summary>
public sealed record TouchMessage(TouchAction Action, ulong PointerId, int X, int Y, int FrameWidth, int FrameHeight, int Pressure) : ControlMessage;

/// <summary>Wheel notches (positive = up/right) at a point, or at the cursor when the frame size is 0.</summary>
public sealed record ScrollMessage(int X, int Y, int FrameWidth, int FrameHeight, int Horizontal, int Vertical) : ControlMessage;

/// <summary>An Android key code with Android meta state; the PC maps it to a Windows key.</summary>
public sealed record KeyMessage(KeyAction Action, int KeyCode, int RepeatCount, int MetaState) : ControlMessage;

public sealed record TextMessage(string Value) : ControlMessage;

public sealed record NavigateMessage(NavigationTarget Target) : ControlMessage;

public sealed record KeyframeRequestMessage : ControlMessage;

/// <summary>The viewer turned; 0 portrait, 1 landscape.</summary>
public sealed record RotateMessage(int Orientation) : ControlMessage;

/// <summary>Trackpad movement in frame pixels.</summary>
public sealed record MouseMoveMessage(int Dx, int Dy) : ControlMessage;

public sealed record MouseButtonMessage(MouseButton Button, KeyAction Action) : ControlMessage;

/// <summary>
/// Input sent between the viewer and the device that is being shown, one message per media
/// channel control record. Big-endian; <c>dev.baton.android.stream.ControlMessageCodec</c> is the
/// byte-identical twin, pinned by parity tests on both sides.
/// </summary>
public static class ControlMessages
{
    public const int PressureScale = 65_535;
    public const int MaxTextBytes = 64 * 1024;

    public static byte[] Encode(ControlMessage message)
    {
        switch (message)
        {
            case TouchMessage touch:
            {
                var bytes = new byte[1 + 1 + 8 + 4 + 4 + 2 + 2 + 2];
                bytes[0] = (byte)ControlMessageType.Touch;
                bytes[1] = (byte)touch.Action;
                BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(2), touch.PointerId);
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(10), touch.X);
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(14), touch.Y);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(18), (ushort)touch.FrameWidth);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), (ushort)touch.FrameHeight);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22), (ushort)touch.Pressure);
                return bytes;
            }

            case ScrollMessage scroll:
            {
                var bytes = new byte[1 + 4 + 4 + 2 + 2 + 2 + 2];
                bytes[0] = (byte)ControlMessageType.Scroll;
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), scroll.X);
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(5), scroll.Y);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(9), (ushort)scroll.FrameWidth);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(11), (ushort)scroll.FrameHeight);
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(13), (short)scroll.Horizontal);
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(15), (short)scroll.Vertical);
                return bytes;
            }

            case KeyMessage key:
            {
                var bytes = new byte[1 + 1 + 4 + 4 + 4];
                bytes[0] = (byte)ControlMessageType.Key;
                bytes[1] = (byte)key.Action;
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(2), key.KeyCode);
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(6), key.RepeatCount);
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(10), key.MetaState);
                return bytes;
            }

            case TextMessage text:
            {
                var utf8 = Encoding.UTF8.GetBytes(text.Value);
                if (utf8.Length > MaxTextBytes)
                {
                    throw new ArgumentException($"Text exceeds {MaxTextBytes} bytes.");
                }

                var bytes = new byte[1 + 4 + utf8.Length];
                bytes[0] = (byte)ControlMessageType.Text;
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), utf8.Length);
                utf8.CopyTo(bytes.AsSpan(5));
                return bytes;
            }

            case NavigateMessage navigate:
                return [(byte)ControlMessageType.Navigate, (byte)navigate.Target];
            case KeyframeRequestMessage:
                return [(byte)ControlMessageType.KeyframeRequest];
            case RotateMessage rotate:
                return [(byte)ControlMessageType.Rotate, (byte)rotate.Orientation];
            case MouseMoveMessage move:
            {
                var bytes = new byte[1 + 4 + 4];
                bytes[0] = (byte)ControlMessageType.MouseMove;
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), move.Dx);
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(5), move.Dy);
                return bytes;
            }

            case MouseButtonMessage button:
                return [(byte)ControlMessageType.MouseButton, (byte)button.Button, (byte)button.Action];
            default:
                throw new ArgumentException($"Unknown control message {message.GetType().Name}.");
        }
    }

    /// <summary>Reads one message from a record payload.</summary>
    public static ControlMessage Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new InvalidDataException("Empty control message.");
        }

        var body = bytes[1..];
        return (ControlMessageType)bytes[0] switch
        {
            ControlMessageType.Touch => new TouchMessage(
                (TouchAction)body[0],
                BinaryPrimitives.ReadUInt64BigEndian(body[1..]),
                BinaryPrimitives.ReadInt32BigEndian(body[9..]),
                BinaryPrimitives.ReadInt32BigEndian(body[13..]),
                BinaryPrimitives.ReadUInt16BigEndian(body[17..]),
                BinaryPrimitives.ReadUInt16BigEndian(body[19..]),
                BinaryPrimitives.ReadUInt16BigEndian(body[21..])),
            ControlMessageType.Scroll => new ScrollMessage(
                BinaryPrimitives.ReadInt32BigEndian(body),
                BinaryPrimitives.ReadInt32BigEndian(body[4..]),
                BinaryPrimitives.ReadUInt16BigEndian(body[8..]),
                BinaryPrimitives.ReadUInt16BigEndian(body[10..]),
                BinaryPrimitives.ReadInt16BigEndian(body[12..]),
                BinaryPrimitives.ReadInt16BigEndian(body[14..])),
            ControlMessageType.Key => new KeyMessage(
                (KeyAction)body[0],
                BinaryPrimitives.ReadInt32BigEndian(body[1..]),
                BinaryPrimitives.ReadInt32BigEndian(body[5..]),
                BinaryPrimitives.ReadInt32BigEndian(body[9..])),
            ControlMessageType.Text => ReadText(body),
            ControlMessageType.Navigate => new NavigateMessage((NavigationTarget)body[0]),
            ControlMessageType.KeyframeRequest => new KeyframeRequestMessage(),
            ControlMessageType.Rotate => new RotateMessage(body[0]),
            ControlMessageType.MouseMove => new MouseMoveMessage(
                BinaryPrimitives.ReadInt32BigEndian(body),
                BinaryPrimitives.ReadInt32BigEndian(body[4..])),
            ControlMessageType.MouseButton => new MouseButtonMessage((MouseButton)body[0], (KeyAction)body[1]),
            _ => throw new InvalidDataException($"Unknown control message type 0x{bytes[0]:x2}.")
        };
    }

    private static TextMessage ReadText(ReadOnlySpan<byte> body)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(body);
        if (length < 0 || length > MaxTextBytes || length > body.Length - 4)
        {
            throw new InvalidDataException($"Control text length {length} is out of range.");
        }

        return new TextMessage(Encoding.UTF8.GetString(body.Slice(4, length)));
    }
}
