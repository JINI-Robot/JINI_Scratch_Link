using Fleck;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.SerialCommunication;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using System.Threading;
using System.IO.Ports;

namespace scratch_link
{
    internal class SRSession : Session
    {
        // Things we can look for are listed here:
        // <a href="https://docs.microsoft.com/en-us/windows/uwp/devices-sensors/device-information-properties" />

        /// <summary>
        /// Signal strength property
        /// </summary>
        private const string SignalStrengthPropertyName = "System.Devices.Aep.SignalStrength"; 

        /// <summary>
        /// Indicates that the device returned is actually available and not discovered from a cache
        /// NOTE: This property is not currently used since it reports 'False' for paired devices
        /// which are currently advertising and within discoverable range.
        /// </summary>
        private const string IsPresentPropertyName = "System.Devices.Aep.IsPresent";

        /// <summary>
        /// 
        /// </summary>
        private const string SerialPortPropertyName = "System.DeviceInterface.Serial.PortName";//"System.Devices.Aep.DeviceAddress";


        //"System.Devices.Aep.Manufacturer"
        //"System.Devices.Aep.ModelName"
        //"System.Devices.Aep.ModelyIds"
        /// <summary>
        /// PIN code for pairing
        /// </summary>
        private string _pairingCode = "1234";

        /// <summary>
        /// PIN code for auto-pairing
        /// </summary>
        private string _autoPairingCode = "1234";

        private DeviceWatcher _watcher;
        private StreamSocket _connectedSocket;
        public DataWriter _socketWriter;
        public DataReader _socketReader;

        public SerialDevice serialDevice;

        private DeviceInformationCollection serialDeviceInfos;

        public SerialPort _serialPort;

        public int _cntAIDeskPortPrev = 0;

        internal SRSession(IWebSocketConnection webSocket) : base(webSocket)
        {
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (_watcher != null &&
                (_watcher.Status == DeviceWatcherStatus.Started ||
                 _watcher.Status == DeviceWatcherStatus.EnumerationCompleted))
            {
                _watcher.Stop();
            }
            if (_connectedSocket != null)
            {
                _socketReader.Dispose();
                _socketWriter.Dispose();
                _connectedSocket.Dispose();
                serialDevice.Dispose();
                //_serialPort.Close();

            }
        }

        protected override async Task DidReceiveCall(string method, JObject parameters,
            Func<JToken, JsonRpcException, Task> completion)
        {
            switch (method)
            {
                case "discover":
                    _cntAIDeskPortPrev = 0;
                    Discover(parameters);
                    await completion(null, null);
                    break;
                case "connect":
                    if (_watcher != null && _watcher.Status == DeviceWatcherStatus.Started)
                    {
                        _watcher.Stop();
                    }
                    await Connect(parameters);
                    await completion(null, null);
                    break;
                case "send":
                    await completion(await SendMessage(parameters), null);   // 메세지 보낼때
                    break;
                default:
                    // unrecognized method: pass to base class
                    await base.DidReceiveCall(method, parameters, completion);
                    break;
            }
        }

        private void Discover(JObject parameters)
        {
            
            try
            {
                var selector = SerialDevice.GetDeviceSelector();
                _watcher = DeviceInformation.CreateWatcher(selector, new List<String>
                {
                    SignalStrengthPropertyName,
                    IsPresentPropertyName,
                    SerialPortPropertyName
                });
                _watcher.Added += PeripheralDiscovered;
                _watcher.EnumerationCompleted += EnumerationCompleted;
                _watcher.Updated += PeripheralUpdated;
                _watcher.Stopped += EnumerationStopped;
                _watcher.Start();           

            }
            catch (ArgumentException)
            {
                throw JsonRpcException.ApplicationError("Failed to create device watcher");
            }

        }
        private async Task Connect(JObject parameters)
        {
            DeviceInformation deviceInfo;


            string selector = SerialDevice.GetDeviceSelector();
            serialDeviceInfos = await DeviceInformation.FindAllAsync(selector);

            if (_connectedSocket?.Information.RemoteHostName != null)
            {
                throw JsonRpcException.InvalidRequest("Already connected");
            }
            var pID = parameters["peripheralId"]?.ToObject<string>();

            if (serialDeviceInfos.Count > 0)
            {
                //DeviceInformation deviceInfo;
                int i = 0;
                
                for (i=0;i< serialDeviceInfos.Count; i++)
                {
                    deviceInfo = serialDeviceInfos[i];
                    if (pID == deviceInfo.Id)
                    {
                        serialDevice = await SerialDevice.FromIdAsync(deviceInfo.Id);// await SerialDevice.FromIdAsync(deviceInfo.Id);
                        serialDevice.BaudRate = 57600;
                        serialDevice.DataBits = 8;
                        serialDevice.StopBits = SerialStopBitCount.One;
                        serialDevice.Parity = SerialParity.None;
                        serialDevice.WriteTimeout = TimeSpan.FromMilliseconds(5000);
                        serialDevice.ReadTimeout = TimeSpan.FromMilliseconds(5000);
                        serialDevice.Handshake = SerialHandshake.None;
                        serialDevice.IsDataTerminalReadyEnabled = true;
                        serialDevice.IsRequestToSendEnabled = true;
                        break;
                    }
                }
            }
            else
            {


            }
           
            if (serialDeviceInfos.Count > 0 && true)
            {
                _connectedSocket = new StreamSocket();
                _socketWriter = new DataWriter(serialDevice.OutputStream);
                _socketReader = new DataReader(serialDevice.InputStream) { ByteOrder = ByteOrder.LittleEndian };
                ListenForMessages();
            }
            else
            {
                throw JsonRpcException.ApplicationError("Cannot read services from peripheral");
            }

        }

        private async Task<JToken> SendMessage(JObject parameters)
        {
            if (_socketWriter == null)
            {
                throw JsonRpcException.InvalidRequest("Not connected to peripheral");
            }

            var data = EncodingHelpers.DecodeBuffer(parameters);
            try
            {
                _socketWriter.WriteBytes(data);
                await _socketWriter.StoreAsync();
            }
            catch (ObjectDisposedException)
            {
                throw JsonRpcException.InvalidRequest("Not connected to peripheral");
            }
            return data.Length;
        }
        
        private async void  ListenForMessages()
        {
            try
            {                
                while (true)
                {                   
                    await _socketReader.LoadAsync(sizeof(UInt16));
                    var messageSize = (uint)64;// _socketReader.ReadUInt16();  
                    var headerBytes = BitConverter.GetBytes(messageSize);

                    var messageBytes = new byte[messageSize];
                    await _socketReader.LoadAsync(messageSize);
                    _socketReader.ReadBytes(messageBytes);

                    _socketReader.DetachBuffer();
                    //_socketReader.DetachStream();

                    var totalBytes = new byte[headerBytes.Length + messageSize];
                    Array.Copy(headerBytes, totalBytes, headerBytes.Length);
                    Array.Copy(messageBytes, 0, totalBytes, headerBytes.Length, messageSize);

                    var parameters = EncodingHelpers.EncodeBuffer(totalBytes, "base64");
                    SendRemoteRequest("didReceiveMessage", parameters);
                                        
                }
            }
            catch (Exception e)
            {
                await SendErrorNotification(JsonRpcException.ApplicationError("Peripheral connection closed"));
                Debug.Print($"Closing connection to peripheral: {e.Message}");
                Dispose();
            }
        }

        #region Custom Pairing Event Handlers

        private void CustomOnPairingRequested(DeviceInformationCustomPairing sender,
            DevicePairingRequestedEventArgs args)
        {
            args.Accept(_pairingCode);
        }

        #endregion

        #region DeviceWatcher Event Handlers

        private void PeripheralDiscovered(DeviceWatcher sender, DeviceInformation deviceInformation)
        {
            // Note that we don't filter out by 'IsPresentPropertyName' here because we need to return devices
            // which are paired and within discoverable range. However, 'IsPresentPropertyName' is set to False
            // for paired devices that are discovered automatically from a cache, so we ignore that property
            // and simply return all discovered devices.


            DeviceInformation di = deviceInformation;

            //deviceInformation.Properties.TryGetValue(SerialPortPropertyName, out var address);
            deviceInformation.Properties.TryGetValue(SignalStrengthPropertyName, out var rssi);
            var peripheralId = di.Id; //((string)address)?.Replace(":", "");

            string name = "";
            string a = peripheralId;


            var idx3 = 0;;
            var idx1 = a.IndexOf("ROOT");
            var idx2 = a.IndexOf("PORTS");
            if (idx1>=0 && idx2>=0)
            {                
                string str = a.Substring(idx2 + 6,4);

                idx3 = int.Parse(str);
                str = "(COM" + idx3.ToString() + ")";
                name = "AIDESK" ;
                _cntAIDeskPortPrev++;

                if (_cntAIDeskPortPrev >= 2) return;
                var peripheralInfo = new JObject
                {
                    new JProperty("peripheralId", peripheralId),
                    new JProperty("name", new JValue(name)),
                    new JProperty("rssi", rssi)
                };
                SendRemoteRequest("didDiscoverPeripheral", peripheralInfo);
            }

            a = di.Name;
            idx1 = a.IndexOf("CP210x");
            if (idx1 >= 0)
            {
                idx3 = a.IndexOf("(COM");
                name = "AIBot USB to UART Bridge " + a.Substring(idx3);
                var peripheralInfo = new JObject
                {
                    new JProperty("peripheralId", peripheralId),
                    new JProperty("name", new JValue(name)),
                    new JProperty("rssi", rssi)
                };
                SendRemoteRequest("didDiscoverPeripheral", peripheralInfo);
            }
            
        }

        /// <summary>
        /// Handle event when a discovered peripheral is updated
        /// </summary>
        /// <remarks>
        /// This method does nothing, but having an event handler for <see cref="DeviceWatcher.Updated"/> seems to
        /// be necessary for timely "didDiscoverPeripheral" notifications. If there is no handler, all discovered
        /// peripherals are notified right before enumeration completes.
        /// </remarks>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void PeripheralUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
        }

        private void EnumerationCompleted(DeviceWatcher sender, object args)
        {
            Debug.Print("Enumeration completed.");
        }

        private void EnumerationStopped(DeviceWatcher sender, object args)
        {
            if (_watcher.Status == DeviceWatcherStatus.Aborted)
            {
                Debug.Print("Enumeration stopped unexpectedly.");
            }
            else if (_watcher.Status == DeviceWatcherStatus.Stopped)
            {
                Debug.Print("Enumeration stopped.");
            }
          
            _watcher.Added -= PeripheralDiscovered;
            _watcher.EnumerationCompleted -= EnumerationCompleted;
            _watcher.Updated -= PeripheralUpdated;
            _watcher.Stopped -= EnumerationStopped;
            _watcher = null;
          
        }

        #endregion
    }
}
