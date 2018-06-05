using System.Collections;
using System.Runtime.InteropServices;

namespace GHIElectronics.TinyCLR.Devices.Gpio.Provider {
    internal class GpioPinEventListener {
        private IDictionary pinMap = new Hashtable();
        private NativeEventDispatcher dispatcher;

        private static string GetKey(string providerName, ulong pin) => $"{providerName}\\{pin}";

        public GpioPinEventListener() {
            this.dispatcher = NativeEventDispatcher.GetDispatcher("GHIElectronics.TinyCLR.NativeEventNames.Gpio.ValueChanged");
            this.dispatcher.OnInterrupt += (pn, ci, d0, d1, d2, ts) => {
                var pin = default(DefaultGpioPinProvider);
                var key = GpioPinEventListener.GetKey(pn, d0);
                var edge = d1 != 0 ? ProviderGpioPinEdge.RisingEdge : ProviderGpioPinEdge.FallingEdge;

                lock (this.pinMap)
                    if (this.pinMap.Contains(key))
                        pin = (DefaultGpioPinProvider)this.pinMap[key];

                if (pin != null)
                    pin.OnPinChangedInternal(edge);
            };
        }

        public void AddPin(string providerName, DefaultGpioPinProvider pin) {
            var key = GpioPinEventListener.GetKey(providerName, (ulong)pin.PinNumber);

            lock (this.pinMap)
                this.pinMap[key] = pin;
        }

        public void RemovePin(string providerName, DefaultGpioPinProvider pin) {
            var key = GpioPinEventListener.GetKey(providerName, (ulong)pin.PinNumber);

            lock (this.pinMap)
                if (this.pinMap.Contains(key))
                    this.pinMap.Remove(key);
        }
    }
}
