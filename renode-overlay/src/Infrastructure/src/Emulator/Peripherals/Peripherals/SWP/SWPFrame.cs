//
// Copyright (c) 2026 Renode-i3c-slave-master contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

namespace Antmicro.Renode.Peripherals.SWP
{
    // The SWP data link layer frame codec (ETSI TS 102 613 clause 8).
    //
    // A frame on the wire is
    //
    //     SOF ('7E') | bit-stuffed( payload | CRC ) | EOF ('7F')
    //
    // with the following rules, all implemented here:
    //   - the bit order of the SWP communication channel is MSB first;
    //   - bit stuffing: after five consecutive bits with the logical value 1 a bit with the logical
    //     value 0 is inserted, so the six/seven-ones runs of the SOF and EOF flags can never occur
    //     inside a frame. If the last five bits of the CRC have the value 1 no stuff bit is added -
    //     the EOF's own leading 0 already breaks the run;
    //   - the CRC is 16 bits, polynomial X^16 + X^12 + X^5 + 1, initial value 'FFFF', and is computed
    //     over the bits between SOF and EOF, both excluded (i.e. over the unstuffed payload);
    //   - between frames the line carries idle bits (logical value 0), at least one.
    //
    // Because stuffing makes a frame a whole number of *bits* rather than bytes, Encode returns the
    // wire image bit-packed MSB first and pads the tail with idle 0 bits up to the next byte boundary.
    // Decode scans that image bitwise, so the padding is harmless and a decoder never needs the exact
    // bit length. This is what makes a frame self-delimiting and lets ISWPPeripheral pass plain
    // byte[] around.
    public static class SWPFrame
    {
        // Start-of-frame flag: '7E' = 0b01111110 (six consecutive ones).
        public const byte Sof = 0x7E;

        // End-of-frame flag: '7F' = 0b01111111 (seven consecutive ones).
        public const byte Eof = 0x7F;

        // CRC-16 generator polynomial X^16 + X^12 + X^5 + 1, MSB-first representation.
        public const ushort CrcPolynomial = 0x1021;

        // CRC-16 initial value.
        public const ushort CrcInitialValue = 0xFFFF;

        // Computes the frame CRC over the payload, MSB first. Check value for the ASCII string
        // "123456789" is '29B1'.
        public static ushort ComputeCrc(IReadOnlyList<byte> payload)
        {
            var crc = (int)CrcInitialValue;
            for(var i = 0; i < payload.Count; i++)
            {
                crc ^= payload[i] << 8;
                for(var bit = 0; bit < 8; bit++)
                {
                    crc = ((crc & 0x8000) != 0) ? ((crc << 1) ^ CrcPolynomial) : (crc << 1);
                    crc &= 0xFFFF;
                }
            }
            return (ushort)crc;
        }

        // Encodes an LLC payload into a complete wire frame (SOF, stuffed payload + CRC, EOF),
        // bit-packed MSB first and padded to a byte boundary with idle 0 bits.
        public static byte[] Encode(byte[] payload)
        {
            payload = payload ?? new byte[0];

            var body = new byte[payload.Length + 2];
            Array.Copy(payload, body, payload.Length);
            var crc = ComputeCrc(payload);
            // The CRC is transmitted most significant byte first, matching the MSB-first bit order.
            body[payload.Length] = (byte)(crc >> 8);
            body[payload.Length + 1] = (byte)crc;

            var bits = new List<bool>((body.Length + 2) * 9);
            AppendByte(bits, Sof);
            AppendStuffed(bits, body);
            AppendByte(bits, Eof);
            return Pack(bits);
        }

        // Decodes a wire frame. Returns false (with a reason in error) rather than throwing, so a
        // controller or target can log a malformed frame and carry on - which is exactly what the
        // spec's frame-resend / REJ mechanisms exist for.
        public static bool TryDecode(byte[] wire, out byte[] payload, out string error)
        {
            payload = null;
            error = null;
            if(wire == null || wire.Length == 0)
            {
                error = "empty wire image";
                return false;
            }

            var bits = Unpack(wire);

            // Locate the SOF: a 0, six 1s, then a 0. (The EOF has seven 1s, so it can never match.)
            var sofEnd = -1;
            for(var start = FindOnesRun(bits, 0, 6); start >= 0; start = FindOnesRun(bits, start + 1, 6))
            {
                if(start >= 1 && !bits[start - 1] && start + 6 < bits.Count && !bits[start + 6])
                {
                    sofEnd = start + 7;
                    break;
                }
            }
            if(sofEnd < 0)
            {
                error = "no SOF flag found";
                return false;
            }

            // Locate the EOF: the first run of seven 1s. Stuffing guarantees the frame body never
            // holds more than five consecutive 1s, so this run can only be the EOF flag, and the bit
            // immediately before it is the flag's own leading 0.
            var eofOnes = FindOnesRun(bits, sofEnd, 7);
            if(eofOnes < 1 || bits[eofOnes - 1])
            {
                error = "no EOF flag found";
                return false;
            }

            if(!TryDestuff(bits, sofEnd, eofOnes - 1, out var body, out error))
            {
                return false;
            }
            if((body.Count % 8) != 0)
            {
                error = $"frame body is not a whole number of bytes ({body.Count} bits)";
                return false;
            }

            var data = Pack(body);
            if(data.Length < 2)
            {
                error = "frame is shorter than its CRC";
                return false;
            }

            var received = (ushort)((data[data.Length - 2] << 8) | data[data.Length - 1]);
            var decoded = new byte[data.Length - 2];
            Array.Copy(data, decoded, decoded.Length);
            var expected = ComputeCrc(decoded);
            if(received != expected)
            {
                error = $"CRC mismatch: received 0x{received:X4}, computed 0x{expected:X4}";
                return false;
            }

            payload = decoded;
            return true;
        }

        private static void AppendByte(List<bool> bits, byte value)
        {
            for(var i = 7; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        // Appends the body bits, inserting a 0 after each run of five 1s. A run of five that ends on
        // the very last bit gets no stuff bit: the EOF flag's leading 0 breaks the run already.
        private static void AppendStuffed(List<bool> bits, byte[] body)
        {
            var totalBits = body.Length * 8;
            var ones = 0;
            for(var i = 0; i < totalBits; i++)
            {
                var bit = ((body[i / 8] >> (7 - (i % 8))) & 1) != 0;
                bits.Add(bit);
                ones = bit ? ones + 1 : 0;
                if(ones == 5)
                {
                    if(i != totalBits - 1)
                    {
                        bits.Add(false);
                    }
                    ones = 0;
                }
            }
        }

        // Removes the stuffed 0s from bits[from, toExclusive).
        private static bool TryDestuff(List<bool> bits, int from, int toExclusive, out List<bool> body, out string error)
        {
            body = new List<bool>(toExclusive - from);
            error = null;
            var ones = 0;
            var i = from;
            while(i < toExclusive)
            {
                var bit = bits[i];
                body.Add(bit);
                ones = bit ? ones + 1 : 0;
                i++;
                if(ones == 5)
                {
                    ones = 0;
                    if(i < toExclusive)
                    {
                        if(bits[i])
                        {
                            error = $"missing stuff bit at bit {i}";
                            return false;
                        }
                        i++;
                    }
                }
            }
            return true;
        }

        // Index of the first bit of the first run of `count` consecutive 1s that starts at or after
        // `from`, or -1 if there is none. Only bits at index >= from are counted into the run.
        private static int FindOnesRun(List<bool> bits, int from, int count)
        {
            var run = 0;
            for(var i = Math.Max(from, 0); i < bits.Count; i++)
            {
                run = bits[i] ? run + 1 : 0;
                if(run == count)
                {
                    return i - count + 1;
                }
            }
            return -1;
        }

        private static byte[] Pack(List<bool> bits)
        {
            var result = new byte[(bits.Count + 7) / 8];
            for(var i = 0; i < bits.Count; i++)
            {
                if(bits[i])
                {
                    result[i / 8] |= (byte)(1 << (7 - (i % 8)));
                }
            }
            return result;
        }

        private static List<bool> Unpack(byte[] data)
        {
            var bits = new List<bool>(data.Length * 8);
            foreach(var b in data)
            {
                for(var i = 7; i >= 0; i--)
                {
                    bits.Add(((b >> i) & 1) != 0);
                }
            }
            return bits;
        }
    }
}
