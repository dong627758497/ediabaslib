using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Reflection;
using System.Text;
using Android.Bluetooth;
using Android.Content;
using Android.Hardware.Usb;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Android.Support.V7.App;
using Android.Widget;
using EdiabasLib;
using Hoho.Android.UsbSerial.Driver;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Android.Content.PM;

namespace BmwDeepObd
{
    public class ActivityCommon : IDisposable
    {
        public class FileSystemBlockInfo
        {
            /// <summary>
            /// The path you asked to check file allocation blocks for
            /// </summary>
            public string Path { get; set; }

            /// <summary>
            /// The file system block size, in bytes, for the given path
            /// </summary>
            public double BlockSizeBytes { get; set; }

            /// <summary>
            /// Total size of the file system at the given path
            /// </summary>
            public double TotalSizeBytes { get; set; }

            /// <summary>
            /// Available size of the file system at the given path
            /// </summary>
            public double AvailableSizeBytes { get; set; }

            /// <summary>
            /// Total free size of the file system at the given path
            /// </summary>
            public double FreeSizeBytes { get; set; }
        }
        
        public enum InterfaceType
        {
            None,
            Bluetooth,
            Enet,
            Ftdi,
        }

        public delegate bool ProgressZipDelegate(int percent);
        public delegate void BcReceiverUpdateDisplayDelegate();
        public delegate void BcReceiverReceivedDelegate(Context context, Intent intent);
        public const string EmulatorEnetIp = "192.168.10.244";
        public const string ActionUsbPermission = "de.holeschak.bmw_deep_obd.USB_PERMISSION";
        private const string MailInfoDownloadUrl = @"http://www.holeschak.de/BmwDeepObd/Mail.xml";

        private bool _disposed;
        private readonly Android.App.Activity _activity;
        private readonly BcReceiverUpdateDisplayDelegate _bcReceiverUpdateDisplayHandler;
        private readonly BcReceiverReceivedDelegate _bcReceiverReceivedHandler;
        private readonly bool _emulator;
        private bool? _usbSupport;
        private static bool _externalPathSet;
        private static string _externalPath;
        private static string _externalWritePath;
        private static string _customStorageMedia;
        private readonly BluetoothAdapter _btAdapter;
        private readonly WifiManager _maWifi;
        private readonly ConnectivityManager _maConnectivity;
        private readonly UsbManager _usbManager;
        private readonly PowerManager _powerManager;
        private PowerManager.WakeLock _wakeLockScreen;
        private PowerManager.WakeLock _wakeLockCpu;
        private Receiver _bcReceiver;
        private InterfaceType _selectedInterface;
        private AlertDialog _activateAlertDialog;
        private AlertDialog _selectMediaAlertDialog;
        private AlertDialog _selectInterfaceAlertDialog;

        public bool Emulator
        {
            get
            {
                return _emulator;
            }
        }

        public bool UsbSupport
        {
            get
            {
                if (_usbSupport == null)
                {
                    try
                    {
                        _usbSupport = (_usbManager != null) && (_usbManager.DeviceList != null) && (Build.VERSION.SdkInt >= BuildVersionCodes.HoneycombMr1);
                    }
                    catch (Exception)
                    {
                        _usbSupport = false;
                    }
                }
                return _usbSupport??false;
            }
        }

        public string ExternalPath
        {
            get
            {
                return _externalPath;
            }
        }

        public string ExternalWritePath
        {
            get
            {
                return _externalWritePath;
            }
        }

        public string CustomStorageMedia
        {
            get
            {
                return _customStorageMedia;
            }
            set
            {
                _customStorageMedia = IsWritable(value) ? value : null;
            }
        }

        public InterfaceType SelectedInterface
        {
            get
            {
                return _selectedInterface;
            }
            set
            {
                _selectedInterface = value;
            }
        }

        public BluetoothAdapter BtAdapter
        {
            get
            {
                return _btAdapter;
            }
        }

        public WifiManager MaWifi
        {
            get
            {
                return _maWifi;
            }
        }

        public ConnectivityManager MaConnectivity
        {
            get
            {
                return _maConnectivity;
            }
        }

        public UsbManager UsbManager
        {
            get { return _usbManager; }
        }

        public PowerManager PowerManager
        {
            get { return _powerManager; }
        }

        public Receiver BcReceiver
        {
            get { return _bcReceiver; }
        }

        public ActivityCommon(Android.App.Activity activity, BcReceiverUpdateDisplayDelegate bcReceiverUpdateDisplayHandler = null, BcReceiverReceivedDelegate bcReceiverReceivedHandler = null)
        {
            _activity = activity;
            _bcReceiverUpdateDisplayHandler = bcReceiverUpdateDisplayHandler;
            _bcReceiverReceivedHandler = bcReceiverReceivedHandler;
            _emulator = IsEmulator();
            if (!_externalPathSet)
            {
                SetStoragePath();
                _externalPathSet = true;
            }

            _btAdapter = BluetoothAdapter.DefaultAdapter;
            _maWifi = (WifiManager)activity.GetSystemService(Context.WifiService);
            _maConnectivity = (ConnectivityManager)activity.GetSystemService(Context.ConnectivityService);
            _usbManager = activity.GetSystemService(Context.UsbService) as UsbManager;
            _powerManager = activity.GetSystemService(Context.PowerService) as PowerManager;
            if (_powerManager != null)
            {
                _wakeLockScreen = _powerManager.NewWakeLock(WakeLockFlags.ScreenDim, "ScreenLock");
                _wakeLockScreen.SetReferenceCounted(false);

                _wakeLockCpu = _powerManager.NewWakeLock(WakeLockFlags.Partial, "PartialLock");
                _wakeLockCpu.SetReferenceCounted(false);
            }
            _selectedInterface = InterfaceType.None;

            if ((_bcReceiverUpdateDisplayHandler != null) || (_bcReceiverReceivedHandler != null))
            {
                _bcReceiver = new Receiver(this);
                activity.RegisterReceiver(_bcReceiver, new IntentFilter(BluetoothAdapter.ActionStateChanged));
                activity.RegisterReceiver(_bcReceiver, new IntentFilter(ConnectivityManager.ConnectivityAction));
                if (UsbSupport)
                {   // usb handling
                    activity.RegisterReceiver(_bcReceiver, new IntentFilter(ActionUsbPermission));
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            // This object will be cleaned up by the Dispose method.
            // Therefore, you should call GC.SupressFinalize to
            // take this object off the finalization queue
            // and prevent finalization code for this object
            // from executing a second time.
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.
            if (!_disposed)
            {
                // If disposing equals true, dispose all managed
                // and unmanaged resources.
                if (disposing)
                {
                    if (_activity != null && _bcReceiver != null)
                    {
                        _activity.UnregisterReceiver(_bcReceiver);
                        _bcReceiver = null;
                    }
                    if (_wakeLockScreen != null)
                    {
                        _wakeLockScreen.Release();
                        _wakeLockScreen.Dispose();
                        _wakeLockScreen = null;
                    }
                    if (_wakeLockCpu != null)
                    {
                        _wakeLockCpu.Release();
                        _wakeLockCpu.Dispose();
                        _wakeLockCpu = null;
                    }
                }

                // Note disposing has been done.
                _disposed = true;
            }
        }

        public string InterfaceName()
        {
            switch (_selectedInterface)
            {
                case InterfaceType.Bluetooth:
                    return _activity.GetString(Resource.String.select_interface_bt);

                case InterfaceType.Enet:
                    return _activity.GetString(Resource.String.select_interface_enet);

                case InterfaceType.Ftdi:
                    return _activity.GetString(Resource.String.select_interface_ftdi);
            }
            return string.Empty;
        }

        public bool IsInterfaceEnabled()
        {
            switch (_selectedInterface)
            {
                case InterfaceType.Bluetooth:
                    if (_btAdapter == null)
                    {
                        return false;
                    }
                    return _btAdapter.IsEnabled;

                case InterfaceType.Enet:
                    if (_maWifi == null)
                    {
                        return false;
                    }
                    return _maWifi.IsWifiEnabled;

                case InterfaceType.Ftdi:
                    return true;
            }
            return false;
        }

        public bool IsInterfaceAvailable()
        {
            switch (_selectedInterface)
            {
                case InterfaceType.Bluetooth:
                    if (_btAdapter == null)
                    {
                        return false;
                    }
                    return _btAdapter.IsEnabled;

                case InterfaceType.Enet:
                    if (_maConnectivity == null)
                    {
                        return false;
                    }
                    NetworkInfo networkInfo = _maConnectivity.ActiveNetworkInfo;
                    if (networkInfo == null)
                    {
                        return false;
                    }
                    return networkInfo.IsConnected;

                case InterfaceType.Ftdi:
                {
                    List<IUsbSerialDriver> availableDrivers = EdFtdiInterface.GetDriverList(_usbManager);
                    if (availableDrivers.Count <= 0)
                    {
                        return false;
                    }
                    if (!_usbManager.HasPermission(availableDrivers[0].Device))
                    {
                        return false;
                    }
                    return true;
                }
            }
            return false;
        }

        public bool AllowCanAdapterConfig(string deviceAddress)
        {
            switch (_selectedInterface)
            {
                case InterfaceType.Bluetooth:
                {
                    if (string.IsNullOrEmpty(deviceAddress))
                    {
                        return false;
                    }
                    string[] stringList = deviceAddress.Split(';');
                    if (stringList.Length > 1)
                    {
                        if (string.Compare(stringList[1], EdBluetoothInterface.Elm327Tag, StringComparison.OrdinalIgnoreCase) == 0)
                        {   // ELM device
                            return false;
                        }
                    }
                    return true;
                }

                case InterfaceType.Ftdi:
                    return true;
            }
            return false;
        }

        public void ShowAlert(string message, int titleId)
        {
            new AlertDialog.Builder(_activity)
            .SetMessage(message)
            .SetTitle(titleId)
            .SetNeutralButton(Resource.String.button_ok, (s, e) => { })
            .Show();
        }

        public void SelectMedia(EventHandler<DialogClickEventArgs> handler)
        {
            if (_selectMediaAlertDialog != null)
            {
                return;
            }
            AlertDialog.Builder builder = new AlertDialog.Builder(_activity);
            builder.SetTitle(Resource.String.select_media);
            ListView listView = new ListView(_activity);

            List<string> mediaNames = GetAllStorageMedia();
            int mediaIndex = 0;
            if (!string.IsNullOrEmpty(_customStorageMedia))
            {
                int index = 0;
                foreach (string name in mediaNames)
                {
                    if (string.CompareOrdinal(name, _customStorageMedia) == 0)
                    {
                        mediaIndex = index + 1;
                        break;
                    }
                    index++;
                }
            }
            List<string> displayNames = new List<string>();
            foreach (string name in mediaNames)
            {
                const int maxLength = 40;
                string displayName = name;
                try
                {
                    FileSystemBlockInfo blockInfo = GetFileSystemBlockInfo(name);
                    displayName = String.Format(new FileSizeFormatProvider(), "{0} ({1:fs1}/{2:fs1} {3})",
                        name, blockInfo.AvailableSizeBytes, blockInfo.TotalSizeBytes, _activity.GetString(Resource.String.free_space));
                    if (displayName.Length > maxLength)
                    {
                        displayName = "..." + displayName.Substring(displayName.Length - maxLength);
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
                displayNames.Add(displayName);
            }
            displayNames.Insert(0, _activity.GetString(Resource.String.default_media));

            ArrayAdapter<string> adapter = new ArrayAdapter<string>(_activity,
                Android.Resource.Layout.SimpleListItemSingleChoice, displayNames);
            listView.Adapter = adapter;
            listView.ChoiceMode = ChoiceMode.Single;
            listView.SetItemChecked(mediaIndex, true);
            builder.SetView(listView);
            builder.SetPositiveButton(Resource.String.button_ok, (sender, args) =>
            {
                switch (listView.CheckedItemPosition)
                {
                    case 0:
                        _customStorageMedia = null;
                        handler(sender, args);
                        break;

                    default:
                        _customStorageMedia = mediaNames[listView.CheckedItemPosition - 1];
                        handler(sender, args);
                        break;
                }
            });
            builder.SetNegativeButton(Resource.String.button_abort, (sender, args) =>
            {
            });
            _selectMediaAlertDialog = builder.Show();
            _selectMediaAlertDialog.DismissEvent += (sender, args) => { _selectMediaAlertDialog = null; };
        }

        public void SelectInterface(EventHandler<DialogClickEventArgs> handler)
        {
            if (_selectInterfaceAlertDialog != null)
            {
                return;
            }
            AlertDialog.Builder builder = new AlertDialog.Builder(_activity);
            builder.SetTitle(Resource.String.select_interface);
            ListView listView = new ListView(_activity);

            List<string> interfaceNames = new List<string>
            {
                _activity.GetString(Resource.String.select_interface_bt),
                _activity.GetString(Resource.String.select_interface_enet)
            };
            if (UsbSupport)
            {
                interfaceNames.Add(_activity.GetString(Resource.String.select_interface_ftdi));
            }
            ArrayAdapter<string> adapter = new ArrayAdapter<string>(_activity,
                Android.Resource.Layout.SimpleListItemSingleChoice, interfaceNames.ToArray());
            listView.Adapter = adapter;
            listView.ChoiceMode = ChoiceMode.Single;
            switch (_selectedInterface)
            {
                case InterfaceType.Bluetooth:
                    listView.SetItemChecked(0, true);
                    break;

                case InterfaceType.Enet:
                    listView.SetItemChecked(1, true);
                    break;

                case InterfaceType.Ftdi:
                    listView.SetItemChecked(2, true);
                    break;
            }
            builder.SetView(listView);
            builder.SetPositiveButton(Resource.String.button_ok, (sender, args) =>
                {
                    switch (listView.CheckedItemPosition)
                    {
                        case 0:
                            _selectedInterface = InterfaceType.Bluetooth;
                            handler(sender, args);
                            break;

                        case 1:
                            _selectedInterface = InterfaceType.Enet;
                            handler(sender, args);
                            break;

                        case 2:
                            _selectedInterface = InterfaceType.Ftdi;
                            handler(sender, args);
                            break;
                    }
                });
            builder.SetNegativeButton(Resource.String.button_abort, (sender, args) =>
                {
                });
            _selectInterfaceAlertDialog = builder.Show();
            _selectInterfaceAlertDialog.DismissEvent += (sender, args) => { _selectInterfaceAlertDialog = null; };
        }

        public void EnableInterface()
        {
            switch (_selectedInterface)
            {
                case InterfaceType.Bluetooth:
                    if (_btAdapter == null)
                    {
                        Toast.MakeText(_activity, Resource.String.bt_not_available, ToastLength.Long).Show();
                        break;
                    }
                    if (!_btAdapter.IsEnabled)
                    {
                        try
                        {
#pragma warning disable 0618
                            _btAdapter.Enable();
#pragma warning restore 0618
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }
                    break;

                case InterfaceType.Enet:
                    if (_maWifi == null)
                    {
                        Toast.MakeText(_activity, Resource.String.wifi_not_available, ToastLength.Long).Show();
                        break;
                    }
                    if (!_maWifi.IsWifiEnabled)
                    {
                        try
                        {
                            _maWifi.SetWifiEnabled(true);
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }
                    break;
            }
        }

        public bool RequestInterfaceEnable(EventHandler handler)
        {
            if (_activateAlertDialog != null)
            {
                return true;
            }
            if (IsInterfaceAvailable())
            {
                return false;
            }
            if (IsInterfaceEnabled())
            {
                return false;
            }
            int resourceId;
            switch (_selectedInterface)
            {
                case InterfaceType.Bluetooth:
                    resourceId = Resource.String.bt_enable;
                    break;

                case InterfaceType.Enet:
                    resourceId = Resource.String.wifi_enable;
                    break;

                default:
                    return false;
            }
            _activateAlertDialog = new AlertDialog.Builder(_activity)
                .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                {
                    EnableInterface();
                })
                .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                {
                })
                .SetCancelable(true)
                .SetMessage(resourceId)
                .SetTitle(Resource.String.alert_title_question)
                .Show();
            _activateAlertDialog.DismissEvent += (sender, args) =>
            {
                _activateAlertDialog = null;
                handler(sender, args);
            };
            return true;
        }

        public bool RequestBluetoothDeviceSelect(int requestCode, EventHandler<DialogClickEventArgs> handler)
        {
            if (SelectedInterface != InterfaceType.Bluetooth)
            {
                return true;
            }
            if (!IsInterfaceAvailable())
            {
                return true;
            }
            new AlertDialog.Builder(_activity)
                .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                {
                    if (SelectBluetoothDevice(requestCode))
                    {
                        handler(sender, args);
                    }
                })
                .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                {
                })
                .SetCancelable(true)
                .SetMessage(Resource.String.bt_device_select)
                .SetTitle(Resource.String.alert_title_question)
                .Show();
            return false;
        }

        public bool SelectBluetoothDevice(int requestCode)
        {
            if (!IsInterfaceAvailable())
            {
                return false;
            }
            if (SelectedInterface != InterfaceType.Bluetooth)
            {
                return false;
            }
            Intent serverIntent = new Intent(_activity, typeof(DeviceListActivity));
            _activity.StartActivityForResult(serverIntent, requestCode);
            return true;
        }

        public void RequestUsbPermission(UsbDevice usbDevice)
        {
            if (!UsbSupport)
            {
                return;
            }
            if (usbDevice == null)
            {
                List<IUsbSerialDriver> availableDrivers = EdFtdiInterface.GetDriverList(_usbManager);
                if (availableDrivers.Count > 0)
                {
                    UsbDevice device = availableDrivers[0].Device;
                    if (!_usbManager.HasPermission(device))
                    {
                        usbDevice = device;
                    }
                }
            }
            if (usbDevice != null)
            {
                Android.App.PendingIntent intent = Android.App.PendingIntent.GetBroadcast(_activity, 0, new Intent(ActionUsbPermission), 0);
                _usbManager.RequestPermission(usbDevice, intent);
            }
        }

        public void SetScreenLock(bool enable)
        {
            if (_wakeLockScreen != null)
            {
                try
                {
                    if (enable)
                    {
                        _wakeLockScreen.Acquire();
                    }
                    else
                    {
                        _wakeLockScreen.Release();
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }

        public void SetCpuLock(bool enable)
        {
            if (_wakeLockCpu != null)
            {
                try
                {
                    if (enable)
                    {
                        _wakeLockCpu.Acquire();
                    }
                    else
                    {
                        _wakeLockCpu.Release();
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }

        public void SetEdiabasInterface(EdiabasNet ediabas, string btDeviceAddress)
        {
            object connectParameter = null;
            // ReSharper disable once CanBeReplacedWithTryCastAndCheckForNull
            if (ediabas.EdInterfaceClass is EdInterfaceObd)
            {
                if (SelectedInterface == InterfaceType.Ftdi)
                {
                    ((EdInterfaceObd)ediabas.EdInterfaceClass).ComPort = "FTDI0";
                    connectParameter = new EdFtdiInterface.ConnectParameter(_activity, _usbManager);
                }
                else
                {
                    ((EdInterfaceObd)ediabas.EdInterfaceClass).ComPort = "BLUETOOTH:" + btDeviceAddress;
                }
            }
            else if (ediabas.EdInterfaceClass is EdInterfaceEnet)
            {
                string remoteHost = "auto";
                if (Emulator)
                {   // broadcast is not working with emulator
                    remoteHost = EmulatorEnetIp;
                }
                ((EdInterfaceEnet)ediabas.EdInterfaceClass).RemoteHost = remoteHost;
            }
            ediabas.EdInterfaceClass.ConnectParameter = connectParameter;
        }

        public bool RequestSendTraceFile(string traceDir, PackageInfo packageInfo, EventHandler<EventArgs> handler = null)
        {
            string traceFile = Path.Combine(traceDir, "ifh.trc.zip");

            if (!File.Exists(traceFile))
            {
                return false;
            }
            new AlertDialog.Builder(_activity)
                .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                {
                    SendTraceFile(traceFile, packageInfo, true);
                })
                .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                {
                    if (handler != null)
                    {
                        handler(this, new EventArgs());
                    }
                })
                .SetCancelable(true)
                .SetMessage(Resource.String.send_trace_file_request)
                .SetTitle(Resource.String.alert_title_question)
                .Show();

            return true;
        }

        public bool SendTraceFile(string traceFile, PackageInfo packageInfo, bool deleteFile = false)
        {
            if (!File.Exists(traceFile))
            {
                return false;
            }
            string mailBody = string.Format("Deep OBD Trace file\nDate: {0}\nApp version name: {1}\nApp version code: {2}",
                DateTime.Now.ToString("u"), packageInfo.VersionName, packageInfo.VersionCode);

            Android.App.ProgressDialog progress = new Android.App.ProgressDialog(_activity);
            progress.SetCancelable(false);
            progress.SetMessage(_activity.GetString(Resource.String.zip_trace_file));
            progress.Show();

            Thread sendThread = new Thread(() =>
            {
                bool sendFailed = false;
                try
                {
                    WebClient webClient = new WebClient();
                    SmtpClient smtpClient = new SmtpClient
                    {
                        DeliveryMethod = SmtpDeliveryMethod.Network,
                    };
                    MailMessage mail = new MailMessage()
                    {
                        Subject = "Deep OBD trace file",
                        Body = mailBody,
                        BodyEncoding = Encoding.UTF8
                    };

                    mail.Attachments.Add(new Attachment(traceFile));

                    _activity.RunOnUiThread(() =>
                    {
                        progress.SetMessage(_activity.GetString(Resource.String.send_trace_file));
                    });

                    AutoResetEvent retryEvent = new AutoResetEvent(false);
                    for (; ; )
                    {
                        retryEvent.Reset();
                        try
                        {
                            string mailInfoFile = Path.Combine(Path.GetDirectoryName(traceFile) ?? string.Empty, "Mail.xml");
                            webClient.DownloadFile(MailInfoDownloadUrl, mailInfoFile);

                            string mailHost;
                            int mailPort;
                            bool mailSsl;
                            string mailFrom;
                            string mailTo;
                            string mailUser;
                            string mailPassword;
                            if (!GetMailInfo(mailInfoFile, out mailHost, out mailPort, out mailSsl, out mailFrom, out mailTo, out mailUser, out mailPassword))
                            {
                                throw new Exception("Invalid mail info");
                            }
                            try
                            {
                                File.Delete(mailInfoFile);
                            }
                            catch (Exception)
                            {
                                // ignored
                            }
                            smtpClient.Host = mailHost;
                            smtpClient.Port = mailPort;
                            smtpClient.EnableSsl = mailSsl;
                            smtpClient.UseDefaultCredentials = false;
                            if (string.IsNullOrEmpty(mailUser) || string.IsNullOrEmpty(mailPassword))
                            {
                                smtpClient.Credentials = null;
                            }
                            else
                            {
                                smtpClient.Credentials = new NetworkCredential(mailUser, mailPassword);
                            }
                            mail.From = new MailAddress(mailFrom);
                            mail.To.Clear();
                            mail.To.Add(new MailAddress(mailTo));
                            smtpClient.Send(mail);
                            if (deleteFile)
                            {
                                try
                                {
                                    File.Delete(traceFile);
                                }
                                catch (Exception)
                                {
                                    // ignored
                                }
                            }
                            break;
                        }
                        catch (Exception)
                        {
                            bool retrySend = false;
                            _activity.RunOnUiThread(() =>
                            {
                                AlertDialog altertDialog = new AlertDialog.Builder(_activity)
                                    .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                                    {
                                        retrySend = true;
                                    })
                                    .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                                    {
                                    })
                                    .SetCancelable(true)
                                    .SetMessage(Resource.String.send_trace_file_failed_retry)
                                    .SetTitle(Resource.String.alert_title_error)
                                    .Show();
                                altertDialog.DismissEvent += (sender, args) =>
                                {
                                    // ReSharper disable once AccessToDisposedClosure
                                    retryEvent.Set();
                                };
                            });
                            retryEvent.WaitOne();
                            if (!retrySend)
                            {
                                break;
                            }
                        }
                    }
                    retryEvent.Dispose();
                    File.Delete(traceFile);
                }
                catch (Exception)
                {
                    sendFailed = true;
                }
                _activity.RunOnUiThread(() =>
                {
                    progress.Hide();
                    progress.Dispose();
                    if (sendFailed)
                    {
                        ShowAlert(_activity.GetString(Resource.String.send_trace_file_failed),
                            Resource.String.alert_title_error);
                    }
                });
            });
            sendThread.Start();
            return true;
        }

        private bool GetMailInfo(string xmlFile, out string host, out int port, out bool ssl, out string from, out string to, out string name, out string password)
        {
            host = null;
            port = 0;
            ssl = false;
            from = null;
            to = null;
            name = null;
            password = null;
            try
            {
                if (!File.Exists(xmlFile))
                {
                    return false;
                }
                XDocument xmlDoc = XDocument.Load(xmlFile);
                if (xmlDoc.Root == null)
                {
                    return false;
                }
                XElement emailNode = xmlDoc.Root.Element("email");
                if (emailNode == null)
                {
                    return false;
                }
                XAttribute hostAttr = emailNode.Attribute("host");
                if (hostAttr == null)
                {
                    return false;
                }
                host = hostAttr.Value;
                XAttribute portAttr = emailNode.Attribute("port");
                if (portAttr == null)
                {
                    return false;
                }
                port = XmlConvert.ToInt32(portAttr.Value);
                XAttribute sslAttr = emailNode.Attribute("ssl");
                if (sslAttr == null)
                {
                    return false;
                }
                ssl = XmlConvert.ToBoolean(sslAttr.Value);
                XAttribute fromAttr = emailNode.Attribute("from");
                if (fromAttr == null)
                {
                    return false;
                }
                from = fromAttr.Value;
                XAttribute toAttr = emailNode.Attribute("to");
                if (toAttr == null)
                {
                    return false;
                }
                to = toAttr.Value;
                XAttribute usernameAttr = emailNode.Attribute("username");
                if (usernameAttr != null)
                {
                    name = usernameAttr.Value;
                }
                XAttribute passwordAttr = emailNode.Attribute("password");
                if (passwordAttr != null)
                {
                    password = passwordAttr.Value;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        public static string MakeRelativePath(string fromPath, string toPath)
        {
            if (string.IsNullOrEmpty(fromPath))
            {
                return string.Empty;
            }
            if (string.IsNullOrEmpty(toPath))
            {
                return fromPath;
            }
            System.Uri fromUri = new System.Uri(fromPath);
            System.Uri toUri = new System.Uri(toPath);

            if (fromUri.Scheme != toUri.Scheme) { return toPath; } // path can't be made relative.

            System.Uri relativeUri = fromUri.MakeRelativeUri(toUri);
            String relativePath = System.Uri.UnescapeDataString(relativeUri.ToString());

            if (toUri.Scheme.ToUpperInvariant() == "FILE")
            {
                relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }

            return relativePath;
        }

        public static void ExtractZipFile(string archiveFilenameIn, string outFolder, ProgressZipDelegate progressHandler)
        {
            ZipFile zf = null;
            try
            {
                FileStream fs = File.OpenRead(archiveFilenameIn);
                zf = new ZipFile(fs);
                long index = 0;
                foreach (ZipEntry zipEntry in zf)
                {
                    if (progressHandler != null)
                    {
                        if (progressHandler((int)(100 * index / zf.Count)))
                        {
                            return;
                        }
                    }
                    if (!zipEntry.IsFile)
                    {
                        continue;           // Ignore directories
                    }
                    String entryFileName = zipEntry.Name;
                    // to remove the folder from the entry:- entryFileName = Path.GetFileName(entryFileName);
                    // Optionally match entrynames against a selection list here to skip as desired.
                    // The unpacked length is available in the zipEntry.Size property.

                    byte[] buffer = new byte[4096];     // 4K is optimum
                    Stream zipStream = zf.GetInputStream(zipEntry);

                    // Manipulate the output filename here as desired.
                    String fullZipToPath = Path.Combine(outFolder, entryFileName);
                    string directoryName = Path.GetDirectoryName(fullZipToPath);
                    if (!string.IsNullOrEmpty(directoryName))
                    {
                        Directory.CreateDirectory(directoryName);
                    }

                    // Unzip file in buffered chunks. This is just as fast as unpacking to a buffer the full size
                    // of the file, but does not waste memory.
                    // The "using" will close the stream even if an exception occurs.
                    using (FileStream streamWriter = File.Create(fullZipToPath))
                    {
                        StreamUtils.Copy(zipStream, streamWriter, buffer);
                    }
                    index++;
                }
            }
            finally
            {
                if (zf != null)
                {
                    zf.IsStreamOwner = true; // Makes close also shut the underlying stream
                    zf.Close(); // Ensure we release resources
                }
            }
        }

        public static bool CreateZipFile(string[] inputFiles, string archiveFilenameOut,
            ProgressZipDelegate progressHandler)
        {
            try
            {
                FileStream fsOut = File.Create(archiveFilenameOut);
                ZipOutputStream zipStream = new ZipOutputStream(fsOut);
                zipStream.SetLevel(3);

                try
                {
                    long index = 0;
                    foreach (string filename in inputFiles)
                    {
                        if (progressHandler != null)
                        {
                            if (progressHandler((int)(100 * index / inputFiles.Length)))
                            {
                                return false;
                            }
                        }

                        FileInfo fi = new FileInfo(filename);
                        string entryName = Path.GetFileName(filename);

                        ZipEntry newEntry = new ZipEntry(entryName)
                        {
                            DateTime = fi.LastWriteTime,
                            Size = fi.Length
                        };
                        zipStream.PutNextEntry(newEntry);

                        byte[] buffer = new byte[4096];
                        using (FileStream streamReader = File.OpenRead(filename))
                        {
                            StreamUtils.Copy(streamReader, zipStream, buffer);
                        }
                        zipStream.CloseEntry();
                        index++;
                    }
                }
                finally
                {
                    zipStream.IsStreamOwner = true;
                    zipStream.Close();
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        public static string CreateValidFileName(string s, char replaceChar = '_', char[] includeChars = null)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            if (includeChars != null) invalid = invalid.Union(includeChars).ToArray();
            return string.Join(string.Empty, s.ToCharArray().Select(o => invalid.Contains(o) ? replaceChar : o));
        }

        private static bool IsEmulator()
        {
            string fing = Build.Fingerprint;
            bool isEmulator = false;
            if (fing != null)
            {
                isEmulator = fing.Contains("vbox") || fing.Contains("generic");
            }
            return isEmulator;
        }

        public static void WriteResourceToFile(string resourceName, string fileName)
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (resource != null)
                {
                    using (var file = new FileStream(fileName, FileMode.Create, FileAccess.Write))
                    {
                        resource.CopyTo(file);
                    }
                }
            }
        }

        public static bool IsCommunicationError(string exceptionText)
        {
            if (string.IsNullOrEmpty(exceptionText))
            {
                return false;
            }
            if (exceptionText.Contains("executeJob aborted"))
            {
                return false;
            }
            return true;
        }

        public static List<string> GetAllStorageMedia()
        {
            string procMounts = ReadProcMounts();
            return ParseStorageMedia(procMounts);
        }

        private static void SetStoragePath()
        {
            _externalPath = Android.OS.Environment.ExternalStorageDirectory.AbsolutePath;
            _externalWritePath = string.Empty;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Kitkat)
            {   // writing to external disk is only allowed in special directories.
                Java.IO.File[] externalFilesDirs = Android.App.Application.Context.GetExternalFilesDirs(null);
                if (externalFilesDirs.Length > 0)
                {
                    // index 0 is the internal disk
                    if (externalFilesDirs.Length > 1 && externalFilesDirs[1] != null &&
                        IsWritable(externalFilesDirs[1].AbsolutePath))
                    {
                        _externalWritePath = externalFilesDirs[1].AbsolutePath;
                    }
                    else if (externalFilesDirs[0] != null &&
                        IsWritable(externalFilesDirs[0].AbsolutePath))
                    {
                        _externalWritePath = externalFilesDirs[0].AbsolutePath;
                    }
                }
            }
            else
            {
                string procMounts = ReadProcMounts();
                string sdCardEntry = ParseProcMounts(procMounts, _externalPath);
                if (!string.IsNullOrEmpty(sdCardEntry))
                {
                    _externalPath = sdCardEntry;
                }
            }
        }

        private static string ReadProcMounts()
        {
            try
            {
                string contents = File.ReadAllText("/proc/mounts");
                return contents;
            }
            catch (Exception)
            {
                // ignored
            }
            return string.Empty;
        }

        public static bool IsWritable(string pathToTest)
        {
            bool result = false;

            if (!string.IsNullOrWhiteSpace(pathToTest))
            {
                const string testText = "test text";
                string testFile = Guid.NewGuid() + ".txt";
                string testPath = Path.Combine(pathToTest, testFile);
                try
                {
                    File.WriteAllText(testPath, testText);
                    // check for case insensitive file system
                    if (File.Exists(Path.Combine(pathToTest, testFile.ToUpperInvariant())))
                    {
                        result = true;
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
                finally
                {
                    if (File.Exists(testPath))
                    {
                        File.Delete(testPath);
                    }
                }
            }
            return result;
        }

        private static string ParseProcMounts(string procMounts, string externalPath)
        {
            string sdCardEntry = string.Empty;
            if (!string.IsNullOrWhiteSpace(procMounts))
            {
                List<string> procMountEntries = procMounts.Split('\n', '\r').ToList();
                foreach (string entry in procMountEntries)
                {
                    string[] sdCardEntries = entry.Split(' ');
                    if (sdCardEntries.Length > 2)
                    {
                        string path = sdCardEntries[1];
                        if (path.StartsWith(externalPath, StringComparison.OrdinalIgnoreCase) &&
                            string.Compare(path, externalPath, StringComparison.OrdinalIgnoreCase) != 0)
                        {
                            if (IsWritable(path))
                            {
                                sdCardEntry = path;
                                break;
                            }
                        }
                    }
                }
            }
            return sdCardEntry;
        }

        private static List<string> ParseStorageMedia(string procMounts)
        {
            List<string> sdCardList = new List<string>();
            if (!string.IsNullOrWhiteSpace(procMounts))
            {
                List<string> procMountEntries = procMounts.Split('\n', '\r').ToList();
                // ReSharper disable once LoopCanBeConvertedToQuery
                foreach (string entry in procMountEntries)
                {
                    string[] sdCardEntries = entry.Split(' ');
                    if (sdCardEntries.Length > 2)
                    {
                        string path = sdCardEntries[1];
                        if (IsWritable(path))
                        {
                            sdCardList.Add(path);
                        }
                    }
                }
            }
            return sdCardList;
        }

        public static FileSystemBlockInfo GetFileSystemBlockInfo(string path)
        {
            var statFs = new StatFs(path);
            var fsbi = new FileSystemBlockInfo();
            if (Build.VERSION.SdkInt >= BuildVersionCodes.JellyBeanMr2)
            {
                fsbi.Path = path;
                fsbi.BlockSizeBytes = statFs.BlockSizeLong;
                fsbi.TotalSizeBytes = statFs.BlockCountLong * statFs.BlockSizeLong;
                fsbi.AvailableSizeBytes = statFs.AvailableBlocksLong * statFs.BlockSizeLong;
                fsbi.FreeSizeBytes = statFs.FreeBlocksLong * statFs.BlockSizeLong;
            }
            else // this was deprecated in API level 18 (Android 4.3), so if your device is below level 18, this is what will be used instead.
            {
                fsbi.Path = path;
                // you may want to disable warning about obsoletes, earlier versions of Android are using the deprecated versions
#pragma warning disable 618
                fsbi.BlockSizeBytes = statFs.BlockSize;
                fsbi.TotalSizeBytes = statFs.BlockCount * (long)statFs.BlockSize;
                fsbi.AvailableSizeBytes = statFs.AvailableBlocks * (long)statFs.BlockSize;
                fsbi.FreeSizeBytes = statFs.FreeBlocks * (long)statFs.BlockSize;
#pragma warning restore 618
            }
            return fsbi;
        }

        public static long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }
            // 1.
            // Get array of all file names.
            string[] a = Directory.GetFiles(path, "*.*");

            // 2.
            // Calculate total bytes of all files in a loop.
            return a.Select(name => new FileInfo(name)).Select(info => info.Length).Sum();
        }

        public class Receiver : BroadcastReceiver
        {
            readonly ActivityCommon _activityCommon;

            public Receiver(ActivityCommon activityCommon)
            {
                _activityCommon = activityCommon;
            }

            public override void OnReceive(Context context, Intent intent)
            {
                string action = intent.Action;

                if (_activityCommon._bcReceiverReceivedHandler != null)
                {
                    _activityCommon._bcReceiverReceivedHandler(context, intent);
                }
                switch (action)
                {
                    case BluetoothAdapter.ActionStateChanged:
                    case ConnectivityManager.ConnectivityAction:
                        if (_activityCommon._bcReceiverUpdateDisplayHandler != null)
                        {
                            _activityCommon._bcReceiverUpdateDisplayHandler();
                        }
                        break;

                    case ActionUsbPermission:
                        if (intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false))
                        {
                            if (_activityCommon._bcReceiverUpdateDisplayHandler != null)
                            {
                                _activityCommon._bcReceiverUpdateDisplayHandler();
                            }
                        }
                        break;
                }
            }
        }
    }
}
