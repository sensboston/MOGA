using System;
using System.Linq;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Diagnostics;

namespace MOGAController
{
    public enum ControllerAction
    {
        Pressed,
        Unpressed
    }

    public enum ControllerState
    {
        Connection = 10,
        PowerLow = 11,
        SupportedVersion = 12,
        SelectedVersion = 13
    }

    public enum ControllerResult
    {
        Connected = 0,
        Disconnected = 1,
        Connecting = 2,
        NoMogaDevicePairingFound = 3,
        VersionMoga = 20,
        VersionMogaPro = 21
    }

    public enum Axis
    {
        X = 90,
        Y = 91,
        Z = 92,
        RZ = 93,
        LeftTrigger = 94,
        RightTrigger = 95
    }

    public enum KeyCode
    {
        A = 10,
        B = 11,
        X = 12,
        Y = 13,
        Start = 14,
        Select = 15,
        L1 = 16,
        R1 = 17,
        L2 = 18,
        R2 = 19,
        ThumbLeft = 20,
        ThumbRight = 21,
        DPadUp = 22,
        DPadDown = 23,
        DPadLeft = 24,
        DPadRight = 25
    }

    public delegate void StateChangedEventHandler(StateEvent args);
    public delegate void KeyEventHandler(KeyEvent args);
    public delegate void MotionEventHandler(MotionEvent args);

    public sealed class KeyEvent
    {
        public ControllerAction Action { get; }
        public KeyCode KeyCode { get; }

        public KeyEvent(KeyCode keyCode, ControllerAction action) { KeyCode = keyCode; Action = action; }
    }

    public sealed class MotionEvent
    {
        public Axis Axis { get; }

        public float AxisValue { get; }

        public MotionEvent(Axis axis, float axisValue) { Axis = axis; AxisValue = axisValue; }
    }

    public sealed class StateEvent
    {
        public ControllerResult StateValue { get; }

        public StateEvent(ControllerResult stateValue) { StateValue = stateValue; }
    }

    public sealed class Controller
    {
        private DeviceInformation _serviceInfo;
        private RfcommDeviceService _rfcommService;
        private StreamSocket _socket = new StreamSocket();
        private CancellationTokenSource _cancelationSource;

        private const uint RECVMSG_LEN = 12;
        private const byte SENDMSG_LEN = 5;
        private const uint RECVBUF_LEN = 256;
        private const int  MOGABUF_LEN = 8;

        private const byte CMD_RESET = 65;
        private const byte CMD_INIT = 67;
        private const byte CMD_FLUSH = 68;

        private byte[] m_State = new byte[MOGABUF_LEN];

        public string ControllerName { get; private set; }

        public bool IsConnected { get; private set; }

        private bool _cancelListener = false;
        private bool _cancelConnect = false;

        public event StateChangedEventHandler StateChanged;

        public event MotionEventHandler AxisChanged;

        public event KeyEventHandler KeyChanged;

        public void Disconnect()
        {
            _cancelConnect = _cancelListener = true;
            _socket.Dispose();
            _socket = new StreamSocket();
        }

        public void Connect()
        {
            Connect("");
        }

        public async void Connect(string btServiceName)
        {
            _cancelConnect = _cancelListener = IsConnected = false;
            var serviceInfoCollection = await DeviceInformation.FindAllAsync(RfcommDeviceService.GetDeviceSelector(RfcommServiceId.SerialPort));
            if (string.IsNullOrEmpty(btServiceName))
                _serviceInfo = serviceInfoCollection.Where(s => s.Name.ToUpper().Contains("BD&A") || s.Name.ToUpper().Contains("MOGA")).FirstOrDefault();
            else
                _serviceInfo = serviceInfoCollection.Where(s => s.Name.ToUpper().Contains(btServiceName.ToUpper())).FirstOrDefault();

            if (_serviceInfo != null)
            {
                // Initialize the target Bluetooth RFCOMM device service
                _rfcommService = await RfcommDeviceService.FromIdAsync(_serviceInfo.Id);

                if (_rfcommService != null)
                {
                    StateChanged?.Invoke(new StateEvent(ControllerResult.Connecting));

                    bool skipConnection = false;
                    // For connect retrying 
                    while (!_cancelConnect)
                    {
                        try
                        {
                            if (!skipConnection)
                                await _socket.ConnectAsync(_rfcommService.ConnectionHostName, _rfcommService.ConnectionServiceName, SocketProtectionLevel.PlainSocket);

                            IsConnected = true;
                            StateChanged?.Invoke(new StateEvent(ControllerResult.Connected));
                            Debug.WriteLine("Connected!");

                            // Set controller ID
                            await SendMessage(CMD_INIT);

                            ControllerName = _serviceInfo.Name;

                            await Task.Factory.StartNew(SocketListener, new CancellationTokenSource().Token, TaskCreationOptions.LongRunning, TaskScheduler.FromCurrentSynchronizationContext());

                            return;
                        }
                        catch (Exception error)
                        {
                            // No more data available exception:
                            // probably, controller is off, let's continue our tries
                            if ((uint)error.HResult == 0x80070103) 
                            {
                                Debug.WriteLine(error.Message);
                                await Task.Delay(500);
                            }
                            // Guess "Method called in unexpected time" exception :
                            // We are already connected so we should skip ConnectAsync call
                            else skipConnection = true;
                        }
                    }
                }
                else StateChanged?.Invoke(new StateEvent(ControllerResult.NoMogaDevicePairingFound));
            }
            else StateChanged?.Invoke(new StateEvent(ControllerResult.NoMogaDevicePairingFound));
            Debug.WriteLine("exiting Connect()");
        }

        private async Task SocketListener()
        {
            await SendMessage(CMD_RESET);
            while (!_cancelListener)
            {
                await SendMessage(CMD_FLUSH);
                var retCode = await ReceiveMessage(TimeSpan.FromSeconds(29));
                if (retCode < 1)
                {
                    if (retCode == -3) _cancelListener = true;
                    else await SendMessage(CMD_RESET);
                }
                else ProcessData(); 
            }
            IsConnected = false;
            StateChanged?.Invoke(new StateEvent(ControllerResult.Disconnected));
        }

        #region Sending/receiving data

        // Moga MODE A command codes discovered so far:
        // -Sent TO Moga controller
        //    65 - poll controller state, digital triggers  (12b response)
        //    67 - change controller id
        //    68 - listen mode, digital triggers  (12b response)
        //    69 - poll controller state, analog triggers  (14b response)
        //    70 - listen mode, analog triggers  (14b response)
        // -Recv FROM Moga controller
        //    97 - poll command response, digital triggers  (12b response)
        //   100 - listen mode status update, digital triggers  (12b response)
        //   101 - poll command response, analog triggers  (14b response)
        //   102 - listen mode status update, analog triggers  (14b response)
        // Oddly, there seems to be no way to obtain battery status.  It's reported in HID Mode B, but not here.
        private async Task<int> SendMessage(byte code)
        {
            if (_socket == null) return 1;
            byte i, chksum = 0;
            byte[] msg = new byte[SENDMSG_LEN];

            msg[0] = 0x5a;            // identifier
            msg[1] = SENDMSG_LEN;     // message length - always 5
            msg[2] = code;            // command to send
            msg[3] = 1;               // controller id
            // calculate a check sum
            for (i = 0; i < SENDMSG_LEN - 1; i++)
                chksum = (byte)(msg[i] ^ chksum);
            msg[4] = chksum;

            try
            {
                await _socket.OutputStream.WriteAsync(msg.AsBuffer());
            }
            catch (Exception error)
            {
                Debug.WriteLine("Exception on SendMessage:" + error.Message);
            }
            return 1;
        }

        // Received messages are a similar format to the sent messages:
        //   byte 0 - 0x7a identifier 
        //   byte 1 - length, 12 or 14
        //   byte 2 - message code
        //   byte 3 - controller id
        //   4 - 9 or 11 - data bytes
        //   10 or 12    - 0x10 ..not sure what this means.  Could be identifying the kind of Moga.
        //   11 or 13    - checksum
        // If the message doesn't validate, something is messed up.  Just reset the connection.
        private async Task<int> ReceiveMessage(TimeSpan timeout)
        {
            if (_socket == null) return -1;

            byte i, chksum = 0;
            var recvBuf = new byte[RECVBUF_LEN];

            // Returned data can be 12 or 14 bytes long, so the message length should be checked before a full read.
            // I dislike making assumptions on socket reads, but in the interests of streamlining things as much as possible
            // to maybe cut down on lag, and since we do know what the length will be, I'll hardcode the recv message length.

            if (_cancelationSource != null) _cancelationSource.Dispose();
            _cancelationSource = new CancellationTokenSource();
            _cancelationSource.CancelAfter(timeout);

            try
            {
                var newBuffer = await _socket.InputStream.ReadAsync(recvBuf.AsBuffer(), RECVMSG_LEN, InputStreamOptions.Partial).AsTask(_cancelationSource.Token);

                // Incorrect amount of data: socket error or timeout
                if (newBuffer.Length != RECVMSG_LEN) return -1;

                // Calculate checksum
                for (i = 0; i < RECVMSG_LEN - 1; i++)
                    chksum = (byte)(recvBuf[i] ^ chksum);

                // Received bad data
                if (recvBuf[0] != 0x7a || recvBuf[RECVMSG_LEN - 1] != chksum) return -2;
            }
            catch (Exception error)
            {
                Debug.WriteLine("Exception on ReceiveMessage:" + error.Message);
                return -3; // timeout
            }

            // Copy MOGA data
            Array.Copy(recvBuf, 4, m_State, 0, MOGABUF_LEN);
            return 1;
        }

        private byte buttonA = 0;
        private byte buttonB = 0;
        private byte buttonX = 0;
        private byte buttonY = 0;
        private byte buttonL1 = 0;
        private byte buttonR1 = 0;
        private byte buttonL2 = 0;
        private byte buttonR2 = 0;
        private byte buttonSelect = 0;
        private byte buttonStart = 0;
        private byte buttonDPadUp = 0;
        private byte buttonDPadDown = 0;
        private byte buttonDPadLeft = 0;
        private byte buttonDPadRight = 0;

        private int axisX = 0;
        private int axisY = 0;
        private int axisZ = 0;
        private int axisRZ = 0;

        private sbyte dataX = 0;
        private sbyte dataY = 0;
        private sbyte dataZ = 0;
        private sbyte dataRZ = 0;

        private void ProcessData()
        {
            // First, process buttons
            Debug.WriteLine(BitConverter.ToString(m_State));

            // First, process the buttons
            byte newButtonA = (byte)((m_State[0] >> 2) & 1);            // A
            byte newButtonB = (byte)((m_State[0] >> 1) & 1);            // B
            byte newButtonX = (byte)((m_State[0] >> 3) & 1);            // X
            byte newButtonY = (byte)((m_State[0] >> 0) & 1);            // Y
            byte newButtonL1 = (byte)((m_State[0] >> 6) & 1);           // L1
            byte newButtonR1 = (byte)((m_State[0] >> 7) & 1);           // R1
            byte newButtonL2 = (byte)((m_State[1] >> 4) & 1);           // L2
            byte newButtonR2 = (byte)((m_State[1] >> 5) & 1);           // R2
            byte newButtonSelect = (byte)((m_State[0] >> 5) & 1);       // Select
            byte newButtonStart = (byte)((m_State[0] >> 4) & 1);        // Start
            byte newButtonDPadUp = (byte)(m_State[1]  & 1);             // DPad Up
            byte newButtonDPadDown = (byte)((m_State[1] >> 1) & 1);     // DPad Down
            byte newButtonDPadLeft = (byte)((m_State[1] >> 2) & 1);     // DPad Left
            byte newButtonDPadRight = (byte)((m_State[1] >> 3) & 1);    // DPad Right

            if (buttonA != newButtonA) KeyChanged?.Invoke(new KeyEvent(KeyCode.A, newButtonA > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonB != newButtonB) KeyChanged?.Invoke(new KeyEvent(KeyCode.B, newButtonB > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonX != newButtonX) KeyChanged?.Invoke(new KeyEvent(KeyCode.X, newButtonX > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonY != newButtonY) KeyChanged?.Invoke(new KeyEvent(KeyCode.Y, newButtonY > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonL1 != newButtonL1) KeyChanged?.Invoke(new KeyEvent(KeyCode.L1, newButtonL1 > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonR1 != newButtonR1) KeyChanged?.Invoke(new KeyEvent(KeyCode.R1, newButtonR1 > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonL2 != newButtonL2) KeyChanged?.Invoke(new KeyEvent(KeyCode.L2, newButtonL2 > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonR2 != newButtonR2) KeyChanged?.Invoke(new KeyEvent(KeyCode.R2, newButtonR2 > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonSelect != newButtonSelect) KeyChanged?.Invoke(new KeyEvent(KeyCode.Select, newButtonSelect > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonStart != newButtonStart) KeyChanged?.Invoke(new KeyEvent(KeyCode.Start, newButtonStart > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonDPadUp != newButtonDPadUp) KeyChanged?.Invoke(new KeyEvent(KeyCode.DPadUp, newButtonDPadUp > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonDPadDown != newButtonDPadDown) KeyChanged?.Invoke(new KeyEvent(KeyCode.DPadDown, newButtonDPadDown > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonDPadLeft != newButtonDPadLeft) KeyChanged?.Invoke(new KeyEvent(KeyCode.DPadLeft, newButtonDPadLeft > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));
            if (buttonDPadRight != newButtonDPadRight) KeyChanged?.Invoke(new KeyEvent(KeyCode.DPadRight, newButtonDPadRight > 0 ? ControllerAction.Pressed : ControllerAction.Unpressed));

            buttonA = newButtonA;
            buttonB = newButtonB;
            buttonX = newButtonX;
            buttonY = newButtonY;
            buttonL1 = newButtonL1;
            buttonR1 = newButtonR1;
            buttonL2 = newButtonL2;
            buttonR2 = newButtonR2;
            buttonSelect = newButtonSelect;
            buttonStart = newButtonStart;
            buttonDPadUp = newButtonDPadUp;
            buttonDPadDown = newButtonDPadDown;
            buttonDPadLeft = newButtonDPadLeft;
            buttonDPadRight = newButtonDPadRight;

            // Is it "Moga Mobile" controller?
            if (ControllerName.Equals("BD&A"))
            {
                // Next, process joystick/d-pad axes
                int newAxisX = 0;
                sbyte newDataX = (sbyte)m_State[2];
                if ((byte)(m_State[1] & 0x04) == 0x04 || (byte)(m_State[1] & 0x08) == 0x08) newAxisX = (byte)(m_State[1] & 0x04) == 0x04 ? 1 : -1;
                if (axisX != newAxisX || (newAxisX != 0 && dataX != newDataX) || (axisX != 0 && newAxisX == 0))
                {
                    AxisChanged?.Invoke(new MotionEvent(Axis.X, newAxisX == 0 ? 0 : newDataX));
                }
                axisX = newAxisX;
                dataX = newDataX;

                int newAxisY = 0;
                sbyte newDataY = (sbyte)m_State[3];
                if ((byte)(m_State[1] & 0x01) == 0x01 || (byte)(m_State[1] & 0x02) == 2) newAxisY = (byte)(m_State[1] & 0x01) == 0x01 ? 1 : -1;
                if (axisY != newAxisY || (newAxisY != 0 && dataY != newDataY) || (axisY != 0 && newAxisY == 0))
                {
                    AxisChanged?.Invoke(new MotionEvent(Axis.Y, newAxisY == 0 ? 0 : newDataY));
                }
                axisY = newAxisY;
                dataY = newDataY;

                int newAxisZ = 0;
                sbyte newDataZ = (sbyte)m_State[5];
                if ((byte)(m_State[1] & 0x10) == 0x10 || (byte)(m_State[1] & 0x20) == 0x20) newAxisZ = (byte)(m_State[1] & 0x10) == 0x10 ? 1 : -1;
                if (axisZ != newAxisZ || (newAxisZ != 0 && dataZ != newDataZ) || (axisZ != 0 && newAxisZ == 0))
                {
                    AxisChanged?.Invoke(new MotionEvent(Axis.Z, newAxisZ == 0 ? 0 : newDataZ));
                }
                axisZ = newAxisZ;
                dataZ = newDataZ;

                int newAxisRZ = 0;
                sbyte newDataRZ = (sbyte)m_State[4];
                if ((byte)(m_State[1] & 0x40) == 0x40 || (byte)(m_State[1] & 0x80) == 0x80) newAxisRZ = (byte)(m_State[1] & 0x40) == 0x40 ? 1 : -1;
                if (axisRZ != newAxisRZ || (newAxisRZ != 0 && dataRZ != newDataRZ) || (axisRZ != 0 && newAxisRZ == 0))
                {
                    AxisChanged?.Invoke(new MotionEvent(Axis.RZ, newAxisRZ == 0 ? 0 : newDataRZ));
                }
                axisRZ = newAxisRZ;
                dataRZ = newDataRZ;
            }
            else
            {
                // Next, process joystick/d-pad axes
                int newAxisX = 0;
                sbyte newDataX = (sbyte)m_State[2];
                if ((byte)(m_State[2] & 0x80) == 0x80 || (byte)(m_State[2] & 0x40) == 0x40) newAxisX = (byte)(m_State[2] & 0x80) == 0x80 ? 1 : -1;
                if (axisX != newAxisX || (newAxisX != 0 && dataX != newDataX) || (axisX != 0 && newAxisX == 0))
                {
                    AxisChanged?.Invoke(new MotionEvent(Axis.X, newDataX));
                }
                axisX = newAxisX;
                dataX = newDataX;

                int newAxisY = 0;
                sbyte newDataY = (sbyte)m_State[3];
                if ((byte)(m_State[3] & 0x80) == 0x80 || (byte)(m_State[3] & 0x40) == 0x40) newAxisY = (byte)(m_State[3] & 0x80) == 0x80 ? 1 : -1;
                if (axisY != newAxisY || (newAxisY != 0 && dataY != newDataY) || (axisY != 0 && newAxisY == 0))
                {
                    AxisChanged?.Invoke(new MotionEvent(Axis.Y, newDataY));
                }
                axisY = newAxisY;
                dataY = newDataY;

                int newAxisZ = 0;
                sbyte newDataZ = (sbyte)m_State[5];
                if ((byte)(m_State[5] & 0x80) == 0x80 || (byte)(m_State[5] & 0x40) == 0x40) newAxisZ = (byte)(m_State[5] & 0x80) == 0x80 ? 1 : -1;
                if (axisZ != newAxisZ || (newAxisZ != 0 && dataZ != newDataZ) || (axisZ != 0 && newAxisZ == 0))
                {
                    AxisChanged?.Invoke(new MotionEvent(Axis.Z, newAxisZ == 0 ? 0 : newDataZ));
                }
                axisZ = newAxisZ;
                dataZ = newDataZ;

                int newAxisRZ = 0;
                sbyte newDataRZ = (sbyte)m_State[4];
                if ((byte)(m_State[4] & 0x80) == 0x80 || (byte)(m_State[4] & 0x40) == 0x40) newAxisRZ = (byte)(m_State[4] & 0x80) == 0x80 ? 1 : -1;
                if (axisRZ != newAxisRZ || (newAxisRZ != 0 && dataRZ != newDataRZ) || (axisRZ != 0 && newAxisRZ == 0))
                {
                    AxisChanged?.Invoke(new MotionEvent(Axis.RZ, newAxisRZ == 0 ? 0 : newDataRZ));
                }
                axisRZ = newAxisRZ;
                dataRZ = newDataRZ;

            }
        }

        #endregion 
    }
}