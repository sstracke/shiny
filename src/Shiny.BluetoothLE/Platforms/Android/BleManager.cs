using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Android;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Intrastructure;
using SR = Android.Bluetooth.LE.ScanResult;

namespace Shiny.BluetoothLE;


public class BleManager : ScanCallback, IBleManager, IShinyStartupTask
{
    public const string BroadcastReceiverName = "org.shiny.bluetoothle.ShinyBleCentralBroadcastReceiver";

    readonly AndroidPlatform platform;    
    readonly AndroidBleConfiguration config;
    readonly IServiceProvider services;
    readonly IOperationQueue operations;
    readonly ILogger<IBleManager> logger;
    readonly ILogger<IPeripheral> peripheralLogger;

    public BleManager(
        AndroidPlatform platform,
        AndroidBleConfiguration config,
        IServiceProvider services,
        IOperationQueue operations,
        ILogger<IBleManager> logger,
        ILogger<IPeripheral> peripheralLogger
    )
    {
        this.platform = platform;
        this.config = config;
        this.services = services;
        this.operations = operations;
        this.logger = logger;
        this.peripheralLogger = peripheralLogger;

        this.Native = platform.GetSystemService<BluetoothManager>(Context.BluetoothService);
    }


    public bool IsScanning => this.Native.Adapter!.IsDiscovering;
    public BluetoothManager Native { get; }


    public void Start()
    {
        ShinyBleBroadcastReceiver.Process = async intent =>
        {
            if (intent?.Action == Intent.ActionBootCompleted || intent?.Action == null)
                return;
            Java.Lang.Class.FromType(typeof(BluetoothDevice));

            var device = intent.GetParcel<BluetoothDevice>(BluetoothDevice.ExtraDevice);
            if (device != null)
            {
                var peripheral = this.GetPeripheral(device);

                switch (intent?.Action)
                {
                    case BluetoothDevice.ActionNameChanged: break; // TODO
                    case BluetoothDevice.ActionBondStateChanged: break; // TODO
                    case BluetoothDevice.ActionPairingRequest: break; // TODO
                    case BluetoothDevice.ActionAclConnected:
                        // bg connected
                        await this.services
                            .RunDelegates<IBleDelegate>(
                                x => x.OnPeripheralStateChanged(peripheral),
                                this.logger
                            )
                            .ConfigureAwait(false);
                        break;
                }
            }
        };

        this.platform.RegisterBroadcastReceiver<ShinyBleBroadcastReceiver>(
            BluetoothDevice.ActionNameChanged,
            BluetoothDevice.ActionBondStateChanged,
            BluetoothDevice.ActionPairingRequest,
            BluetoothDevice.ActionAclConnected,
            Intent.ActionBootCompleted
        );
        ShinyBleAdapterStateBroadcastReceiver.Process = async intent =>
        {
            if (intent?.Action != BluetoothAdapter.ActionStateChanged)
                return;

            var newState = (State)intent.GetIntExtra(BluetoothAdapter.ExtraState, -1);
            if (newState == State.Connected || newState == State.Disconnected)
            {
                var status = newState == State.Connected
                    ? AccessState.Available
                    : AccessState.Disabled;

                await this.services
                    .RunDelegates<IBleDelegate>(
                        del => del.OnAdapterStateChanged(status),
                        this.logger
                    )
                    .ConfigureAwait(false);
            }
        };

        this.platform.RegisterBroadcastReceiver<ShinyBleAdapterStateBroadcastReceiver>(
            BluetoothAdapter.ActionStateChanged,
            Intent.ActionBootCompleted
        );
    }

    public IObservable<AccessState> RequestAccess() => Observable.FromAsync(async ct =>
    {
        var versionPermissions = GetPlatformPermissions();

        if (!versionPermissions.All(x => this.platform.IsInManifest(x)))
            return AccessState.NotSetup;

        var results = await this.platform
            .RequestPermissions(versionPermissions)
            .ToTask(ct)
            .ConfigureAwait(false);

        return results.IsSuccess()
            ? this.Native.GetAccessState() // now look at the actual device state
            : AccessState.Denied;
    });


    readonly Subject<(SR? Native, ScanFailure? Failure)> scanSubj = new();
    public IObservable<ScanResult> Scan(ScanConfig? config = null) => this.RequestAccess()
        .Do(x => x.Assert())
        .Select(x => Observable.Create<ScanResult>(ob =>
        {
            if (this.IsScanning)
                throw new InvalidOperationException("There is already an active scan");
        
            this.Clear();

            var disp = this.scanSubj.Subscribe(x =>
            {
                if (x.Failure == null)
                {
                    if (x.Native != null)
                    {
                        ob.OnNext(this.FromNative(x.Native));
                    }
                }
                else
                {
                    ob.OnError(new InvalidOperationException("Scan Error: " + x.Failure));
                }
            });
            this.StartScan(config);

            return () =>
            {
                this.StopScan();
                disp?.Dispose();
            };
        }))
        .Switch();


    public void StopScan()
        => this.Native.Adapter!.BluetoothLeScanner?.StopScan(this);

    public IEnumerable<IPeripheral> GetConnectedPeripherals()
        => this.peripherals.Where(x => x.Value.Status == ConnectionState.Connected).Select(x => x.Value);

    public IPeripheral? GetKnownPeripheral(string peripheralUuid)
        => this.peripherals.Values.FirstOrDefault(x => x.Uuid.Equals(peripheralUuid, StringComparison.InvariantCultureIgnoreCase));

    public override void OnScanResult([GeneratedEnum] ScanCallbackType callbackType, SR? result)
        => this.scanSubj.OnNext((result, null));

    public override void OnScanFailed([GeneratedEnum] ScanFailure errorCode)
        => this.scanSubj.OnNext((null, errorCode));        

    public override void OnBatchScanResults(IList<SR>? results)
    {
        if (results == null)
            return;

        foreach (var result in results)
            this.scanSubj.OnNext((result, null));
    }


    readonly ConcurrentDictionary<string, Peripheral> peripherals = new();
    Peripheral GetPeripheral(BluetoothDevice device) => this.peripherals.GetOrAdd(
        device.Address!,
        x => new Peripheral(this, this.platform, device, this.operations, this.peripheralLogger)
    );


    protected ScanResult FromNative(SR native)
    {
        var peripheral = this.GetPeripheral(native.Device!);
        var ad = new AdvertisementData(native);
        return new ScanResult(peripheral, native.Rssi, ad);
    }


    void StartScan(ScanConfig? config)
    {
        AndroidScanConfig cfg;
        if (config == null)
            cfg = new();
        else if (config is AndroidScanConfig cfg1)
            cfg = cfg1;
        else
            cfg = new AndroidScanConfig(ServiceUuids: config.ServiceUuids);

        var builder = new ScanSettings.Builder();
        builder.SetScanMode(cfg.ScanMode);

        var scanFilters = new List<ScanFilter>();
        if (cfg.ServiceUuids.Length > 0)
        {
            foreach (var uuid in cfg.ServiceUuids)
            {
                var fullUuid = Utils.ToUuidType(uuid);
                var parcel = new ParcelUuid(fullUuid);
                scanFilters.Add(new ScanFilter.Builder()
                    .SetServiceUuid(parcel)!
                    .Build()!
                );
            }
        }

        if (cfg.UseScanBatching && this.Native.Adapter!.IsOffloadedScanBatchingSupported)
            builder.SetReportDelay(100);

        this.Native.Adapter!.BluetoothLeScanner!.StartScan(
            scanFilters,
            builder.Build(),
            this
        );
    }


    void Clear()
    {
        var connectedDevices = this.peripherals.Values.Select(x => x).ToList(); 
        this.peripherals.Clear();
        foreach (var dev in connectedDevices)
            this.peripherals.TryAdd(dev.Native.Address!, dev);
    }


    static string[] GetPlatformPermissions()
    {
        if (OperatingSystemShim.IsAndroidVersionAtLeast(31))
        {
            return new[]
            {
                Manifest.Permission.BluetoothScan,
                Manifest.Permission.BluetoothConnect
            };
        }
        return new[]
        {
            Manifest.Permission.Bluetooth,
            Manifest.Permission.BluetoothAdmin,
            Manifest.Permission.AccessFineLocation
        };
    }


    //static void Assert(AccessState access)
    //{
    //    if (access == AccessState.NotSetup)
    //    {
    //        var permissions = GetPlatformPermissions();
    //        var msgList = String.Join(", ", permissions);
    //        throw new InvalidOperationException("Your AndroidManifest.xml is missing 1 or more of the following permissions for this version of Android: " + msgList);
    //    }
    //    else if (access != AccessState.Available)
    //    {
    //        throw new InvalidOperationException($"Invalid Status: {access}");
    //    }
    //}
}

//    public IObservable<IEnumerable<IPeripheral>> GetPairedPeripherals() => Observable.Return(this.context
//        .Manager
//        .Adapter!
//        .BondedDevices
//        .Where(x => x.Type == BluetoothDeviceType.Dual || x.Type == BluetoothDeviceType.Le)
//        .Select(this.context.GetDevice)
//    );


//    public IObservable<Intent> ListenForMe(Peripheral me) => this
//        .peripheralSubject
//        .Where(x => x.Peripheral.Native.Address!.Equals(me.Native.Address))
//        .Select(x => x.Intent);


//    public IObservable<Intent> ListenForMe(string eventName, Peripheral me) => this
//        .ListenForMe(me)
//        .Where(intent => intent.Action?.Equals(
//            eventName,
//            StringComparison.InvariantCultureIgnoreCase
//        ) ?? false);


//    public IEnumerable<Peripheral> GetConnectedDevices()
//    {
//        var nativeDevices = this.Manager.GetDevicesMatchingConnectionStates(ProfileType.Gatt, new[]
//        {
//            (int) ProfileState.Connecting,
//            (int) ProfileState.Connected
//        });
//        foreach (var native in nativeDevices)
//            yield return this.GetDevice(native);
//    }
