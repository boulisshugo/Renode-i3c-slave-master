// Minimal stand-ins for the Renode API surface the SWP models use, so the real SWP sources can be
// type-checked without a full Renode checkout. Signatures mirror the ones the existing I3C/SPI
// models in this repo compile against.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Antmicro.Migrant
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Field, AllowMultiple = false)]
    public class TransientAttribute : Attribute { }
}

namespace Antmicro.Renode.Exceptions
{
    public class RecoverableException : Exception
    {
        public RecoverableException(string message) : base(message) { }
    }
}

namespace Antmicro.Renode.Core
{
    using Antmicro.Renode.Peripherals;

    public interface IEmulationElement { }

    public interface IExternal : IEmulationElement { }

    public interface IGPIO
    {
        void Set();
        void Unset();
        bool IsSet { get; }
    }

    public class GPIO : IGPIO
    {
        public void Set() { state = true; }
        public void Set(bool value) { state = value; }
        public void Unset() { state = false; }
        public bool IsSet { get { return state; } }
        private bool state;
    }

    public interface IMachine
    {
        void HandleTimeDomainEvent<T>(Action<T> handler, T state, bool timeDomainInternalEvent);
    }

    public class Emulation
    {
        public ExternalsManager ExternalsManager { get; } = new ExternalsManager();
    }

    public class ExternalsManager
    {
        public void AddExternal(IExternal external, string name) { }
    }

    public static class MachineExtensions
    {
        public static IMachine GetMachine(this IPeripheral peripheral) { return null; }
    }
}

namespace Antmicro.Renode.Core.Structure
{
    using Antmicro.Renode.Core;
    using Antmicro.Renode.Peripherals;

    public class NumberRegistrationPoint<T>
    {
        public NumberRegistrationPoint(T address) { Address = address; }
        public T Address { get; private set; }
    }

    public abstract class SimpleContainer<T> : IPeripheral where T : class, IPeripheral
    {
        protected SimpleContainer(IMachine machine) { this.machine = machine; }

        public virtual void Register(T peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            ChildCollection.Add(registrationPoint.Address, peripheral);
        }

        public virtual void Unregister(T peripheral)
        {
            foreach(var key in ChildCollection.Where(x => ReferenceEquals(x.Value, peripheral)).Select(x => x.Key).ToArray())
            {
                ChildCollection.Remove(key);
            }
        }

        public virtual void Reset() { }

        protected bool TryGetByAddress(int address, out T peripheral)
        {
            return ChildCollection.TryGetValue(address, out peripheral);
        }

        protected readonly Dictionary<int, T> ChildCollection = new Dictionary<int, T>();
        private readonly IMachine machine;
    }
}

namespace Antmicro.Renode.Logging
{
    using Antmicro.Renode.Core;

    public enum LogLevel { Noisy, Debug, Info, Warning, Error }

    public static class Logger
    {
        // Records every log line so the harness can assert on the exact text the robot tests wait for.
        public static readonly System.Collections.Generic.List<string> Entries = new System.Collections.Generic.List<string>();

        public static void Log(this IEmulationElement e, LogLevel level, string message, params object[] args)
        {
            Entries.Add(level + ": " + (args == null || args.Length == 0 ? message : string.Format(message, args)));
        }

        public static void Log(this Antmicro.Renode.Peripherals.IPeripheral p, LogLevel level, string message, params object[] args)
        {
            Entries.Add(level + ": " + (args == null || args.Length == 0 ? message : string.Format(message, args)));
        }
    }
}

namespace Antmicro.Renode.Peripherals
{
    using Antmicro.Renode.Core;

    public interface IPeripheral : IEmulationElement
    {
        void Reset();
    }

    public interface IKnownSize
    {
        long Size { get; }
    }

    public interface INumberedGPIOOutput
    {
        IReadOnlyDictionary<int, IGPIO> Connections { get; }
    }
}

namespace Antmicro.Renode.Peripherals.Bus
{
    using Antmicro.Renode.Peripherals;

    public interface IDoubleWordPeripheral : IPeripheral
    {
        uint ReadDoubleWord(long offset);
        void WriteDoubleWord(long offset, uint value);
    }
}

namespace Antmicro.Renode.Utilities
{
    public static class Misc
    {
        public static byte[] HexStringToByteArray(string hex)
        {
            hex = hex ?? string.Empty;
            var result = new byte[hex.Length / 2];
            for(var i = 0; i < result.Length; i++)
            {
                result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return result;
        }

        public static string PrettyPrintCollectionHex(IEnumerable<byte> collection)
        {
            return "[" + string.Join(", ", collection.Select(x => "0x" + x.ToString("X"))) + "]";
        }
    }

    public class SocketServerProvider
    {
        public SocketServerProvider(bool telnetMode = true, string serverName = null) { }
        public int BufferSize { get; set; }
        public event Action<int> ConnectionAccepted;
        public event Action ConnectionClosed;
        public event Action<byte[]> DataBlockReceived;
        public void Start(int port) { }
        public void Stop() { }
        public void Send(byte[] data) { }
    }
}
