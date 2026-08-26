//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SWP
{
    // Which way a frame crossed the wire, from the point of view of the peripheral that recorded it.
    public enum SWPFrameDirection
    {
        Received,
        Sent,
    }

    // One frame as it crossed the wire, captured for tracing.
    //
    // WireFrame is the raw on-wire image - SOF, bit-stuffed body, CRC, EOF, bit-packed MSB first.
    // Payload is that frame's decoded LLC payload, starting with the control field, and is empty when
    // the frame could not be decoded (a bad CRC, a missing flag) - which is exactly the case a trace
    // is most useful for, so such frames are recorded rather than dropped silently.
    public class SWPFrameRecord
    {
        public SWPFrameRecord(SWPFrameDirection direction, byte[] wireFrame, byte[] payload, string description)
        {
            Direction = direction;
            WireFrame = wireFrame ?? new byte[0];
            Payload = payload ?? new byte[0];
            Description = description;
        }

        public SWPFrameDirection Direction { get; }

        // The raw on-wire image, exactly as SWPFrame.Encode produced it.
        public byte[] WireFrame { get; }

        // The decoded LLC payload (control field first), or empty when the frame was malformed.
        public byte[] Payload { get; }

        // Human-readable name of the frame: "ACT_SYNC", "I N(S)=0 N(R)=1 +2B", "RR N(R)=2", ...
        public string Description { get; }

        // True when the frame could not be decoded - Payload is empty and Description says why.
        public bool IsMalformed => Payload.Length == 0;

        public string WireHex => Misc.PrettyPrintCollectionHex(WireFrame);

        public string PayloadHex => Misc.PrettyPrintCollectionHex(Payload);

        // Compact one-line form used by the monitor-readable trace.
        public override string ToString()
        {
            return string.Format("{0,-4} {1,-26} {2}",
                Direction == SWPFrameDirection.Received ? "in" : "out",
                ToPlainHex(WireFrame),
                Description);
        }

        private static string ToPlainHex(byte[] data)
        {
            var chars = new char[data.Length * 2];
            for(var i = 0; i < data.Length; i++)
            {
                chars[i * 2] = Nibble(data[i] >> 4);
                chars[i * 2 + 1] = Nibble(data[i] & 0xF);
            }
            return new string(chars);
        }

        private static char Nibble(int value)
        {
            return (char)(value < 10 ? '0' + value : 'A' + value - 10);
        }
    }
}
