using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using KRPC.Server;
using KRPC.Server.Net;
using UnityEngine;

namespace KRPC.UI
{
    sealed class MainWindow : Window
    {
        public KRPCConfiguration Config { private get; set; }

        public KRPCServer Server { private get; set; }

        public ClientDisconnectDialog ClientDisconnectDialog { private get; set; }

        /// <summary>
        /// Errors to display
        /// </summary>
        public List<string> Errors { get; private set; }

        public event EventHandler OnStartServerPressed;
        public event EventHandler OnStopServerPressed;

        Dictionary<IClient, long> lastClientActivity = new Dictionary<IClient, long> ();
        const long lastActivityMillisecondsInterval = 100L;
        int numClientsDisplayed;
        // Editable fields
        string address;
        bool manualAddress;
        List<string> availableAddresses;
        string rpcPort;
        string streamPort;
        string maxTimePerUpdate;
        string recvTimeout;
        // Style settings
        readonly Color errorColor = Color.yellow;
        GUIStyle labelStyle, stretchyLabelStyle, textFieldStyle, stretchyTextFieldStyle, buttonStyle,
            toggleStyle, separatorStyle, lightStyle, errorLabelStyle, comboOptionsStyle, comboOptionStyle;
        const float windowWidth = 288f;
        const float addressWidth = 106f;
        const int addressMaxLength = 15;
        const float portWidth = 45f;
        const int portMaxLength = 5;
        const float maxTimePerUpdateWidth = 45f;
        const int maxTimePerUpdateMaxLength = 5;
        const float recvTimeoutWidth = 45f;
        const int recvTimeoutMaxLength = 5;
        // Text strings
        const string startButtonText = "Start server";
        const string stopButtonText = "Stop server";
        const string serverOnlineText = "Server online";
        const string serverOfflineText = "Server offline";
        const string addressLabelText = "Address:";
        const string rpcPortLabelText = "RPC port:";
        const string streamPortLabelText = "Stream port:";
        const string autoStartServerText = "Auto-start server";
        const string autoAcceptConnectionsText = "Auto-accept new clients";
        const string adaptiveRateControlText = "Adaptive rate control";
        const string maxTimePerUpdateText = "Max. time per update";
        const string blockingRecvText = "Blocking receives";
        const string recvTimeoutText = "Receive timeout";
        const string noClientsConnectedText = "No clients connected";
        const string unknownClientNameText = "<unknown>";
        const string invalidAddressText = "Invalid IP address. Must be in dot-decimal notation, e.g. \"192.168.1.0\"";
        const string invalidRPCPortText = "RPC port must be between 0 and 65535";
        const string invalidStreamPortText = "Stream port must be between 0 and 65535";
        const string invalidMaxTimePerUpdateText = "Max. time per update must be an integer";
        const string invalidRecvTimeoutText = "Receive timeout must be an integer";
        const string localClientOnlyText = "Local clients only";
        const string subnetAllowedText = "Subnet {0}";
        const string unknownClientsAllowedText = "Unknown visibility";
        const string autoAcceptingConnectionsText = "auto-accepting new clients";
        const string stringSeparatorText = ", ";

        protected override void Init ()
        {
            Title = "kRPC Server";

            Server.OnClientActivity += (s, e) => SawClientActivity (e.Client);

            Style.fixedWidth = windowWidth;

            var skin = Skin.DefaultSkin;

            labelStyle = new GUIStyle (skin.label);
            labelStyle.margin = new RectOffset (0, 0, 0, 0);

            stretchyLabelStyle = new GUIStyle (skin.label);
            stretchyLabelStyle.margin = new RectOffset (0, 0, 0, 0);
            stretchyLabelStyle.stretchWidth = true;

            textFieldStyle = new GUIStyle (skin.textField);
            textFieldStyle.margin = new RectOffset (0, 0, 0, 0);

            stretchyTextFieldStyle = new GUIStyle (skin.textField);
            stretchyTextFieldStyle.margin = new RectOffset (0, 0, 0, 0);
            stretchyTextFieldStyle.stretchWidth = true;

            buttonStyle = new GUIStyle (skin.button);
            buttonStyle.margin = new RectOffset (0, 0, 0, 0);

            toggleStyle = new GUIStyle (skin.toggle);
            labelStyle.margin = new RectOffset (0, 0, 0, 0);
            toggleStyle.stretchWidth = false;
            toggleStyle.contentOffset = new Vector2 (4, 0);

            separatorStyle = GUILayoutExtensions.SeparatorStyle (new Color (0f, 0f, 0f, 0.25f));
            separatorStyle.fixedHeight = 2;
            separatorStyle.stretchWidth = true;
            separatorStyle.margin = new RectOffset (2, 2, 3, 3);

            lightStyle = GUILayoutExtensions.LightStyle ();

            errorLabelStyle = new GUIStyle (skin.label);
            errorLabelStyle.margin = new RectOffset (0, 0, 0, 0);
            errorLabelStyle.stretchWidth = true;
            errorLabelStyle.normal.textColor = errorColor;

            comboOptionsStyle = GUILayoutExtensions.ComboOptionsStyle ();
            comboOptionStyle = GUILayoutExtensions.ComboOptionStyle ();

            Errors = new List<string> ();
            address = Config.Address.ToString ();
            rpcPort = Config.RPCPort.ToString ();
            streamPort = Config.StreamPort.ToString ();
            maxTimePerUpdate = Config.MaxTimePerUpdate.ToString ();
            recvTimeout = Config.RecvTimeout.ToString ();

            // Get list of available addresses for drop down
            var interfaceAddresses = NetworkInformation.GetLocalIPAddresses ().Select (x => x.ToString ()).ToList ();
            interfaceAddresses.Remove ("127.0.0.1");
            availableAddresses = new List<string> (new [] { "localhost" });
            availableAddresses.AddRange (interfaceAddresses);
            availableAddresses.Add ("Manual");
        }

        void DrawServerStatus ()
        {
            GUILayoutExtensions.Light (Server.Running, lightStyle);
            if (Server.Running)
                GUILayout.Label (serverOnlineText, stretchyLabelStyle);
            else
                GUILayout.Label (serverOfflineText, stretchyLabelStyle);
        }

        void DrawStartStopButton ()
        {
            if (Server.Running) {
                if (GUILayout.Button (stopButtonText, buttonStyle)) {
                    if (OnStopServerPressed != null)
                        OnStopServerPressed (this, EventArgs.Empty);
                    // Force window to resize to height of content
                    // TODO: better way to do this?
                    Position = new Rect (Position.x, Position.y, Position.width, 0f);
                }
            } else {
                if (GUILayout.Button (startButtonText, buttonStyle)) {
                    if (StartServer () && OnStartServerPressed != null)
                        OnStartServerPressed (this, EventArgs.Empty);
                }
            }
        }

        void DrawAddress ()
        {
            if (Server.Running)
                GUILayout.Label (addressLabelText + " " + Server.Address, labelStyle);
            else {
                GUILayout.Label (addressLabelText, labelStyle);
                textFieldStyle.fixedWidth = addressWidth;
                // Get the index of the address in the combo box
                int selected;
                if (!manualAddress && address == IPAddress.Loopback.ToString ())
                    selected = 0;
                else if (!manualAddress && availableAddresses.Contains (address))
                    selected = availableAddresses.IndexOf (address);
                else
                    selected = availableAddresses.Count () - 1;
                // Display the combo box
                selected = GUILayoutExtensions.ComboBox ("address", selected, availableAddresses, buttonStyle, comboOptionsStyle, comboOptionStyle);
                // Get the address from the combo box selection
                if (selected == 0) {
                    address = IPAddress.Loopback.ToString ();
                    manualAddress = false;
                } else if (selected < availableAddresses.Count () - 1) {
                    address = availableAddresses [selected];
                    manualAddress = false;
                } else {
                    // Display a text field when "Manual" is selected
                    address = GUILayout.TextField (address, addressMaxLength, stretchyTextFieldStyle);
                    manualAddress = true;
                }
            }
        }

        void DrawRPCPort ()
        {
            if (Server.Running)
                GUILayout.Label (rpcPortLabelText + " " + Server.RPCPort, labelStyle);
            else {
                GUILayout.Label (rpcPortLabelText, labelStyle);
                textFieldStyle.fixedWidth = portWidth;
                rpcPort = GUILayout.TextField (rpcPort, portMaxLength, textFieldStyle);
            }
        }

        void DrawStreamPort ()
        {
            if (Server.Running)
                GUILayout.Label (streamPortLabelText + " " + Server.StreamPort, labelStyle);
            else {
                GUILayout.Label (streamPortLabelText, labelStyle);
                textFieldStyle.fixedWidth = portWidth;
                streamPort = GUILayout.TextField (streamPort, portMaxLength, textFieldStyle);
            }
        }

        void DrawAutoStartServerToggle ()
        {
            bool autoStartServer = GUILayout.Toggle (Config.AutoStartServer, autoStartServerText, toggleStyle, new GUILayoutOption[] { });
            if (autoStartServer != Config.AutoStartServer) {
                Config.AutoStartServer = autoStartServer;
                Config.Save ();
            }
        }

        void DrawAutoAcceptConnectionsToggle ()
        {
            bool autoAcceptConnections = GUILayout.Toggle (Config.AutoAcceptConnections, autoAcceptConnectionsText, toggleStyle, new GUILayoutOption[] { });
            if (autoAcceptConnections != Config.AutoAcceptConnections) {
                Config.AutoAcceptConnections = autoAcceptConnections;
                Config.Save ();
            }
        }

        void DrawAdaptiveRateControlToggle ()
        {
            bool adaptiveRateControl = GUILayout.Toggle (Config.AdaptiveRateControl, adaptiveRateControlText, toggleStyle, new GUILayoutOption[] { });
            if (adaptiveRateControl != Config.AdaptiveRateControl) {
                Config.AdaptiveRateControl = adaptiveRateControl;
                Config.Save ();
            }
        }

        void DrawMaxTimePerUpdate ()
        {
            if (Server.Running)
                GUILayout.Label (maxTimePerUpdateText + " " + Server.MaxTimePerUpdate, labelStyle);
            else {
                GUILayout.Label (maxTimePerUpdateText, labelStyle);
                textFieldStyle.fixedWidth = maxTimePerUpdateWidth;
                maxTimePerUpdate = GUILayout.TextField (maxTimePerUpdate, maxTimePerUpdateMaxLength, textFieldStyle);
            }
        }

        void DrawBlockingRecvToggle ()
        {
            bool blockingUpdate = GUILayout.Toggle (Config.BlockingRecv, blockingRecvText, toggleStyle, new GUILayoutOption[] { });
            if (blockingUpdate != Config.BlockingRecv) {
                Config.BlockingRecv = blockingUpdate;
                Config.Save ();
            }
        }

        void DrawRecvTimeout ()
        {
            if (Server.Running)
                GUILayout.Label (recvTimeoutText + " " + Server.RecvTimeout, labelStyle);
            else {
                GUILayout.Label (recvTimeoutText, labelStyle);
                textFieldStyle.fixedWidth = maxTimePerUpdateWidth;
                recvTimeout = GUILayout.TextField (recvTimeout, recvTimeoutMaxLength, textFieldStyle);
            }
        }

        void DrawServerInfo ()
        {
            string info = AllowedClientsString (Server.Address);
            if (Config.AutoAcceptConnections)
                info = info + stringSeparatorText + autoAcceptingConnectionsText;
            GUILayout.Label (info, labelStyle);
        }

        void DrawClientsList ()
        {
            // Get list of client descriptions
            IDictionary<IClient,string> clientDescriptions = new Dictionary<IClient,string> ();
            if (Server.Clients.Any ()) {
                foreach (var client in Server.Clients) {
                    try {
                        string clientName = (client.Name == "") ? unknownClientNameText : client.Name;
                        clientDescriptions [client] = clientName + " @ " + client.Address;
                    } catch (ClientDisconnectedException) {
                    }
                }
            }

            // Display the list of clients
            if (clientDescriptions.Any ()) {
                foreach (var entry in clientDescriptions) {
                    var client = entry.Key;
                    var description = entry.Value;
                    GUILayout.BeginHorizontal ();
                    GUILayoutExtensions.Light (IsClientActive (client), lightStyle);
                    GUILayout.Label (description, stretchyLabelStyle);
                    if (GUILayout.Button (new GUIContent (Icons.Instance.buttonDisconnectClient, "Disconnect client"),
                            buttonStyle, GUILayout.MaxWidth (20), GUILayout.MaxHeight (20))) {
                        ClientDisconnectDialog.Show (client);
                    }
                    GUILayout.EndHorizontal ();
                }
            } else {
                GUILayout.BeginHorizontal ();
                GUILayout.Label (noClientsConnectedText, labelStyle);
                GUILayout.EndHorizontal ();
            }
        }

        static String BytesToString (long byteCount)
        {
            string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
            if (byteCount == 0)
                return "0" + suf [0];
            long bytes = Math.Abs (byteCount);
            int place = Convert.ToInt32 (Math.Floor (Math.Log (bytes, 1024)));
            double num = Math.Round (bytes / Math.Pow (1024, place), 1);
            return (Math.Sign (byteCount) * num) + suf [place];
        }

        void DrawPerformanceStats ()
        {
            GUILayout.Label ("Statistics", labelStyle);
            GUILayout.Label (String.Format ("Data read: {0}", BytesToString (Server.BytesRead)), labelStyle);
            GUILayout.Label (String.Format ("Data written: {0}", BytesToString (Server.BytesWritten)), labelStyle);
            GUILayout.Label (String.Format ("Data read rate: {0}/s", BytesToString ((long)Server.BytesReadRate)), labelStyle);
            GUILayout.Label (String.Format ("Data written rate: {0}/s", BytesToString ((long)Server.BytesWrittenRate)), labelStyle);
            GUILayout.Label (String.Format ("RPCs executed: {0}", Server.RPCsExecuted), labelStyle);
            GUILayout.Label (String.Format ("RPC rate: {0} RPC/s", Math.Round (Server.RPCRate)), labelStyle);
            GUILayout.Label (String.Format ("Time per RPC update: {0:F5} s", Server.TimePerRPCUpdate), labelStyle);
            GUILayout.Label (String.Format ("Poll time per RPC update: {0:F5} s", Server.PollTimePerRPCUpdate), labelStyle);
            GUILayout.Label (String.Format ("Exec time per RPC update: {0:F5} s", Server.ExecTimePerRPCUpdate), labelStyle);
            GUILayout.Label (String.Format ("Time per Stream update: {0:F5} s", Server.TimePerStreamUpdate), labelStyle);
            if (Server.AdaptiveRateControl)
                GUILayout.Label (String.Format ("Max time per RPC update: {0} ms", Server.MaxTimePerUpdate), labelStyle);
        }

        protected override void Draw ()
        {
            // Force window to resize to height of content when length of client list changes
            // TODO: better way to do this?
            if (Server.Clients.Count () != numClientsDisplayed) {
                Position = new Rect (Position.x, Position.y, Position.width, 0f);
                numClientsDisplayed = Server.Clients.Count ();
            }

            GUILayout.BeginVertical ();

            GUILayout.BeginHorizontal ();
            DrawServerStatus ();
            DrawStartStopButton ();
            GUILayout.EndHorizontal ();

            GUILayout.Space (4);

            GUILayout.BeginHorizontal ();
            DrawAddress ();
            GUILayout.EndHorizontal ();

            GUILayout.BeginHorizontal ();
            DrawRPCPort ();
            GUILayout.Space (4);
            DrawStreamPort ();
            GUILayout.EndHorizontal ();

            if (Server.Running) {
                GUILayout.BeginHorizontal ();
                DrawServerInfo ();
                GUILayout.EndHorizontal ();
                GUILayoutExtensions.Separator (separatorStyle);
                DrawClientsList ();

                GUILayoutExtensions.Separator (separatorStyle);
                DrawPerformanceStats ();
            } else {
                GUILayout.BeginHorizontal ();
                DrawAutoStartServerToggle ();
                GUILayout.EndHorizontal ();

                GUILayout.BeginHorizontal ();
                DrawAutoAcceptConnectionsToggle ();
                GUILayout.EndHorizontal ();

                GUILayout.BeginHorizontal ();
                DrawAdaptiveRateControlToggle ();
                GUILayout.EndHorizontal ();

                GUILayout.BeginHorizontal ();
                DrawMaxTimePerUpdate ();
                GUILayout.EndHorizontal ();

                GUILayout.BeginHorizontal ();
                DrawBlockingRecvToggle ();
                GUILayout.EndHorizontal ();

                GUILayout.BeginHorizontal ();
                DrawRecvTimeout ();
                GUILayout.EndHorizontal ();

                foreach (var error in Errors)
                    GUILayout.Label (error, errorLabelStyle);
            }
            GUILayout.EndVertical ();
            GUI.DragWindow ();
        }

        bool StartServer ()
        {
            // Validate the settings
            Errors.Clear ();
            IPAddress ignoreAddress;
            ushort ignoreInt;
            bool validAddress = IPAddress.TryParse (address, out ignoreAddress);
            bool validRPCPort = UInt16.TryParse (rpcPort, out ignoreInt);
            bool validStreamPort = UInt16.TryParse (streamPort, out ignoreInt);
            bool validMaxTimePerUpdate = UInt16.TryParse (maxTimePerUpdate, out ignoreInt);
            bool validRecvTimeout = UInt16.TryParse (recvTimeout, out ignoreInt);

            // Display error message if required
            if (!validAddress)
                Errors.Add (invalidAddressText);
            if (!validRPCPort)
                Errors.Add (invalidRPCPortText);
            if (!validStreamPort)
                Errors.Add (invalidStreamPortText);
            if (!validMaxTimePerUpdate)
                Errors.Add (invalidMaxTimePerUpdateText);
            if (!validRecvTimeout)
                Errors.Add (invalidRecvTimeoutText);

            // Save the settings and trigger start server event
            if (Errors.Count == 0) {
                Config.Load ();
                Config.Address = IPAddress.Parse (address);
                Config.RPCPort = Convert.ToUInt16 (rpcPort);
                Config.StreamPort = Convert.ToUInt16 (streamPort);
                Config.MaxTimePerUpdate = Convert.ToUInt16 (maxTimePerUpdate);
                Config.RecvTimeout = Convert.ToUInt16 (recvTimeout);
                Config.Save ();
                return true;
            }
            return false;
        }

        void SawClientActivity (IClient client)
        {
            lastClientActivity [client] = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
        }

        bool IsClientActive (IClient client)
        {
            if (!lastClientActivity.ContainsKey (client))
                return false;
            long now = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            long lastActivity = lastClientActivity [client];
            return now - lastActivityMillisecondsInterval < lastActivity;
        }

        static string AllowedClientsString (IPAddress localAddress)
        {
            if (IPAddress.IsLoopback (localAddress))
                return localClientOnlyText;
            try {
                var subnet = NetworkInformation.GetSubnetMask (localAddress);
                return String.Format (subnetAllowedText, subnet);
            } catch (NotImplementedException) {
            } catch (ArgumentException) {
            } catch (DllNotFoundException) {
            }
            return unknownClientsAllowedText;
        }
    }
}

