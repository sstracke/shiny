using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using CoreBluetooth;
using Foundation;
using Shiny.BluetoothLE.Internals;


namespace Shiny.BluetoothLE
{
    public class BleManager : AbstractBleManager
    {
        readonly CentralContext context;
        public BleManager(CentralContext context) => this.context = context;


        public override bool IsScanning => this.context.Manager.IsScanning;


        public override IObservable<AccessState> RequestAccess() => Observable.Create<AccessState>(ob =>
        {
            IDisposable? disp = null;
            if (this.context.Manager.State == CBCentralManagerState.Unknown)
            {
                disp = this.context
                    .StateUpdated
                    .Subscribe(_ =>
                    {
                        var current = this.context.Manager.State.FromNative();
                        ob.Respond(current);
                    });
            }
            else
            {
                ob.Respond(this.context.Manager.State.FromNative());
            }
            return () => disp?.Dispose();
        });


        public override AccessState Status
            => this.context.Manager.State.FromNative();


        public override IObservable<IPeripheral?> GetKnownPeripheral(string peripheralUuid)
        {
            var uuid = new NSUuid(peripheralUuid);
            var peripheral = this.context
                .Manager
                .RetrievePeripheralsWithIdentifiers(uuid)
                .FirstOrDefault();

            if (peripheral == null)
                return Observable.Return<IPeripheral?>(null);

            var device = this.context.GetPeripheral(peripheral);
            return Observable.Return(device);
        }


        public override IObservable<IEnumerable<IPeripheral>> GetConnectedPeripherals(string? serviceUuid = null)
        {
            if (serviceUuid == null)
                return Observable.Return(this.context.GetConnectedDevices().ToList());

            var list = new List<IPeripheral>();
            var peripherals = this.context.Manager.RetrieveConnectedPeripherals(CBUUID.FromString(serviceUuid));
            foreach (var peripheral in peripherals)
            {
                var dev = this.context.GetPeripheral(peripheral);
                list.Add(dev);
            }
            return Observable.Return(list);
        }


        public override IObservable<AccessState> WhenStatusChanged() => this.context
            .StateUpdated
            .Select(_ => this.Status)
            .StartWith(this.Status);


        public override IObservable<ScanResult> Scan(ScanConfig? config = null)
        {
            if (this.IsScanning)
                throw new ArgumentException("There is already an existing scan");

            return this.RequestAccess()
                .Do(access =>
                {
                    if (access != AccessState.Available)
                        throw new PermissionException(BleLogCategory.BluetoothLE, access);

                    this.IsScanning = true;
                })
                .SelectMany(_ =>
                {
                    config ??= new ScanConfig();
                    if (config.ServiceUuids == null || config.ServiceUuids.Count == 0)
                    {
                        this.context.Manager.ScanForPeripherals(null, new PeripheralScanningOptions { AllowDuplicatesKey = true });
                    }
                    else
                    {
                        var uuids = config.ServiceUuids.Select(CBUUID.FromString).ToArray();
                        this.context.Manager.ScanForPeripherals(uuids, new PeripheralScanningOptions { AllowDuplicatesKey = true });
                    }
                    return this.context.ScanResultReceived;
                })
                .Finally(() =>
                {
                    this.context.Manager.StopScan();
                    this.IsScanning = false;
                });
        }


        public override void StopScan() => this.context.Manager.StopScan();
	}
}