using System;
using System.Net;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using KRPC.Server;
using KRPC.Server.Net;
using KRPC.Server.RPC;
using KRPC.Server.Stream;
using KRPC.Schema.KRPC;
using KRPC.Service;
using KRPC.Continuations;
using KRPC.Utils;

namespace KRPC
{
    /// <summary>
    /// The kRPC server
    /// </summary>
    public class KRPCServer : IServer
    {
        readonly TCPServer rpcTcpServer;
        readonly TCPServer streamTcpServer;
        readonly RPCServer rpcServer;
        readonly StreamServer streamServer;

        IScheduler<IClient<Request,Response>> clientScheduler;
        IList<RequestContinuation> continuations;
        IDictionary<IClient<byte,StreamMessage>, IList<StreamRequest>> streamRequests;

        internal delegate double UniversalTimeFunction ();

        internal UniversalTimeFunction GetUniversalTime;

        /// <summary>
        /// Event triggered when the server starts
        /// </summary>
        public event EventHandler OnStarted;

        /// <summary>
        /// Event triggered when the server stops
        /// </summary>
        public event EventHandler OnStopped;

        /// <summary>
        /// Event triggered when a client is requesting a connection
        /// </summary>
        public event EventHandler<ClientRequestingConnectionArgs> OnClientRequestingConnection;

        /// <summary>
        /// Event triggered when a client has connected
        /// </summary>
        public event EventHandler<ClientConnectedArgs> OnClientConnected;

        /// <summary>
        /// Event triggered when a client performs some activity
        /// </summary>
        public event EventHandler<ClientActivityArgs> OnClientActivity;

        /// <summary>
        /// Event triggered when a client has disconnected
        /// </summary>
        public event EventHandler<ClientDisconnectedArgs> OnClientDisconnected;

        /// <summary>
        /// Stores the context in which a continuation is executed.
        /// For example, used by a continuation to find out which client made the request.
        /// </summary>
        public static class Context
        {
            /// <summary>
            /// The server instance
            /// </summary>
            public static KRPCServer Server { get; private set; }

            /// <summary>
            /// The current client
            /// </summary>
            public static IClient RPCClient { get; private set; }

            /// <summary>
            /// The current game scene
            /// </summary>
            public static GameScene GameScene { get; private set; }

            internal static void Set (KRPCServer server, IClient rpcClient)
            {
                Server = server;
                RPCClient = rpcClient;
            }

            internal static void Clear ()
            {
                Server = null;
                RPCClient = null;
            }

            internal static void SetGameScene (GameScene gameScene)
            {
                GameScene = gameScene;
            }
        }

        internal KRPCServer (IPAddress address, ushort rpcPort, ushort streamPort,
                             bool adaptiveRateControl = true, int maxTimePerUpdate = 10, bool blockingRecv = true, int recvTimeout = 1)
        {
            rpcTcpServer = new TCPServer ("RPCServer", address, rpcPort);
            streamTcpServer = new TCPServer ("StreamServer", address, streamPort);
            rpcServer = new RPCServer (rpcTcpServer);
            streamServer = new StreamServer (streamTcpServer);
            clientScheduler = new RoundRobinScheduler<IClient<Request,Response>> ();
            continuations = new List<RequestContinuation> ();
            streamRequests = new Dictionary<IClient<byte,StreamMessage>,IList<StreamRequest>> ();

            AdaptiveRateControl = adaptiveRateControl;
            MaxTimePerUpdate = maxTimePerUpdate;
            BlockingRecv = blockingRecv;
            RecvTimeout = recvTimeout;

            // Tie events to underlying server
            rpcServer.OnStarted += (s, e) => {
                if (OnStarted != null)
                    OnStarted (this, EventArgs.Empty);
            };
            rpcServer.OnStopped += (s, e) => {
                if (OnStopped != null)
                    OnStopped (this, EventArgs.Empty);
            };
            rpcServer.OnClientRequestingConnection += (s, e) => {
                if (OnClientRequestingConnection != null)
                    OnClientRequestingConnection (s, e);
            };
            rpcServer.OnClientConnected += (s, e) => {
                if (OnClientConnected != null)
                    OnClientConnected (s, e);
            };
            rpcServer.OnClientDisconnected += (s, e) => {
                if (OnClientDisconnected != null)
                    OnClientDisconnected (s, e);
            };

            // Add/remove clients from the scheduler
            rpcServer.OnClientConnected += (s, e) => clientScheduler.Add (e.Client);
            rpcServer.OnClientDisconnected += (s, e) => clientScheduler.Remove (e.Client);

            // Add/remove clients from the list of stream requests
            streamServer.OnClientConnected += (s, e) => streamRequests [e.Client] = new List<StreamRequest> ();
            streamServer.OnClientDisconnected += (s, e) => streamRequests.Remove (e.Client);

            // Validate stream client identifiers
            streamServer.OnClientRequestingConnection += (s, e) => {
                if (rpcServer.Clients.Where (c => c.Guid == e.Client.Guid).Any ())
                    e.Request.Allow ();
                else
                    e.Request.Deny ();
            };
        }

        /// <summary>
        /// Start the server
        /// </summary>
        public void Start ()
        {
            rpcServer.Start ();
            streamServer.Start ();
            ClearStats ();
        }

        /// <summary>
        /// Stop the server
        /// </summary>
        public void Stop ()
        {
            rpcServer.Stop ();
            streamServer.Stop ();
            ObjectStore.Clear ();
        }

        /// <summary>
        /// Get/set the servers listen address
        /// </summary>
        public IPAddress Address {
            get { return rpcTcpServer.Address; }
            set {
                rpcTcpServer.Address = value;
                streamTcpServer.Address = value;
            }
        }

        /// <summary>
        /// Get/set the RPC port
        /// </summary>
        public ushort RPCPort {
            get { return rpcTcpServer.Port; }
            set { rpcTcpServer.Port = value; }
        }

        /// <summary>
        /// Get/set the Stream port
        /// </summary>
        public ushort StreamPort {
            get { return streamTcpServer.Port; }
            set { streamTcpServer.Port = value; }
        }

        public bool AdaptiveRateControl { get; set; }

        public int MaxTimePerUpdate { get; set; }

        public bool BlockingRecv { get; set; }

        public int RecvTimeout { get; set; }

        /// <summary>
        /// Returns true if the server is running
        /// </summary>
        public bool Running {
            get { return rpcServer.Running && streamServer.Running; }
        }

        /// <summary>
        /// Returns a list of clients the server knows about. Note that they might
        /// not be connected to the server.
        /// </summary>
        public IEnumerable<IClient> Clients {
            get { return rpcServer.Clients.Select (x => (IClient)x); }
        }

        ExponentialMovingAverage rpcRate = new ExponentialMovingAverage ();
        ExponentialMovingAverage timePerRPCUpdate = new ExponentialMovingAverage ();
        ExponentialMovingAverage pollTimePerRPCUpdate = new ExponentialMovingAverage ();
        ExponentialMovingAverage execTimePerRPCUpdate = new ExponentialMovingAverage ();
        ExponentialMovingAverage timePerStreamUpdate = new ExponentialMovingAverage ();
        ExponentialMovingAverage bytesReadRate = new ExponentialMovingAverage ();
        ExponentialMovingAverage bytesWrittenRate = new ExponentialMovingAverage ();

        Stopwatch updateTimer = Stopwatch.StartNew ();

        void ClearStats ()
        {
            RPCsExecuted = 0;
            RPCRate = 0;
            TimePerRPCUpdate = 0;
            ExecTimePerRPCUpdate = 0;
            PollTimePerRPCUpdate = 0;
            TimePerStreamUpdate = 0;
        }

        /// <summary>
        /// Get the total number of bytes read from the network.
        /// </summary>
        public long BytesRead {
            get { return rpcServer.BytesRead + streamServer.BytesRead; }
        }

        /// <summary>
        /// Get the total number of bytes written to the network.
        /// </summary>
        public long BytesWritten {
            get { return rpcServer.BytesWritten + streamServer.BytesWritten; }
        }

        /// <summary>
        /// Get the total number of bytes read from the network.
        /// </summary>
        public float BytesReadRate {
            get { return bytesReadRate.Value; }
            set { bytesReadRate.Update (value); }
        }

        /// <summary>
        /// Get the total number of bytes written to the network.
        /// </summary>
        public float BytesWrittenRate {
            get { return bytesWrittenRate.Value; }
            set { bytesWrittenRate.Update (value); }
        }

        /// <summary>
        /// Total number of RPCs executed.
        /// </summary>
        public long RPCsExecuted { get; private set; }

        /// <summary>
        /// Number of RPCs processed per second.
        /// </summary>
        public float RPCRate {
            get { return rpcRate.Value; }
            set { rpcRate.Update (value); }
        }

        /// <summary>
        /// Time taken by the update loop per update, in seconds.
        /// </summary>
        public float TimePerRPCUpdate {
            get { return timePerRPCUpdate.Value; }
            set { timePerRPCUpdate.Update (value); }
        }

        /// <summary>
        /// Time taken polling for new RPCs per update, in seconds.
        /// </summary>
        public float PollTimePerRPCUpdate {
            get { return pollTimePerRPCUpdate.Value; }
            set { pollTimePerRPCUpdate.Update (value); }
        }

        /// <summary>
        /// Time taken polling executing RPCs per update, in seconds.
        /// </summary>
        public float ExecTimePerRPCUpdate {
            get { return execTimePerRPCUpdate.Value; }
            set { execTimePerRPCUpdate.Update (value); }
        }

        /// <summary>
        /// Time taken by the update loop per update, in seconds.
        /// </summary>
        public float TimePerStreamUpdate {
            get { return timePerStreamUpdate.Value; }
            set { timePerStreamUpdate.Update (value); }
        }

        /// <summary>
        /// Update the server
        /// </summary>
        public void Update ()
        {
            long startRPCsExecuted = RPCsExecuted;
            long startBytesRead = BytesRead;
            long startBytesWritten = BytesWritten;

            RPCServerUpdate ();
            StreamServerUpdate ();

            var timeElapsed = updateTimer.ElapsedSeconds ();
            updateTimer.Reset ();
            updateTimer.Start ();

            RPCRate = (float)((double)(RPCsExecuted - startRPCsExecuted) / timeElapsed);
            BytesReadRate = (float)((double)(BytesRead - startBytesRead) / timeElapsed);
            BytesWrittenRate = (float)((double)(BytesWritten - startBytesWritten) / timeElapsed);

            //FIXME: make this work...
            if (AdaptiveRateControl) {
                if (timeElapsed > 1d / 20d) {
                    if (MaxTimePerUpdate > 1)
                        MaxTimePerUpdate--;
                } else {
                    if (MaxTimePerUpdate < 17)
                        MaxTimePerUpdate++;
                }
            }
        }

        /// <summary>
        /// Update the RPC server, called once every FixedUpdate.
        /// This method receives and executes RPCs, for up to MaxTimePerUpdate milliseconds.
        /// RPCs are delayed to the next update if this time expires. If AdaptiveRateControl
        /// is true, MaxTimePerUpdate will be automatically adjusted to achieve a target framerate.
        /// If NonBlockingUpdate is false, this call will block waiting for new RPCs for up to
        /// MaxPollTimePerUpdate milliseconds. If NonBlockingUpdate is true, a single non-blocking call
        /// will be made to check for new RPCs.
        /// </summary>
        void RPCServerUpdate ()
        {
            var timer = Stopwatch.StartNew ();
            var pollTimeout = new Stopwatch ();
            var pollTimer = new Stopwatch ();
            var execTimer = new Stopwatch ();
            int rpcsExecuted = 0;

            var yieldedContinuations = new List<RequestContinuation> ();
            rpcServer.Update ();

            while (true) {

                // Poll for RPCs
                pollTimer.Start ();
                pollTimeout.Reset ();
                pollTimeout.Start ();
                while (true) {
                    PollRequests (yieldedContinuations);
                    if (!BlockingRecv)
                        break;
                    if (pollTimeout.ElapsedMilliseconds > RecvTimeout)
                        break;
                    if (timer.ElapsedMilliseconds > MaxTimePerUpdate)
                        break;
                    if (continuations.Any ())
                        break;
                }
                pollTimer.Stop ();

                if (!continuations.Any ())
                    break;

                // Execute RPCs
                execTimer.Start ();
                foreach (var continuation in continuations) {

                    // Ignore the continuation if the client has disconnected
                    if (!continuation.Client.Connected)
                        continue;                                    

                    // Max exec time exceeded, delay to next update
                    if (timer.ElapsedMilliseconds > MaxTimePerUpdate) {
                        yieldedContinuations.Add (continuation);
                        continue;
                    }

                    // Execute the continuation
                    try {
                        ExecuteContinuation (continuation);
                    } catch (YieldException e) {
                        yieldedContinuations.Add ((RequestContinuation)e.Continuation);
                    }
                    rpcsExecuted++;
                }
                continuations.Clear ();
                execTimer.Stop ();

                // Exit if max exec time exceeded
                if (timer.ElapsedMilliseconds > MaxTimePerUpdate)
                    break;
            }

            // Run yielded continuations on the next update
            continuations = yieldedContinuations;

            timer.Stop ();

            RPCsExecuted += rpcsExecuted;
            TimePerRPCUpdate = (float)timer.ElapsedSeconds ();
            PollTimePerRPCUpdate = (float)pollTimer.ElapsedSeconds ();
            ExecTimePerRPCUpdate = (float)execTimer.ElapsedSeconds ();
        }

        /// <summary>
        /// Update the Stream server. Executes all streaming RPCs and sends the results to clients.
        /// </summary>
        void StreamServerUpdate ()
        {
            Stopwatch timer = Stopwatch.StartNew ();
            int rpcsExecuted = 0;

            streamServer.Update ();

            // Run streaming requests
            foreach (var entry in streamRequests) {
                var streamClient = entry.Key;
                var requests = entry.Value;
                if (!requests.Any ())
                    continue;
                var streamMessage = StreamMessage.CreateBuilder ();
                foreach (var request in requests) {
                    Response.Builder response;
                    try {
                        response = KRPC.Service.Services.Instance.HandleRequest (request.Procedure, request.Arguments);
                    } catch (Exception e) {
                        response = Response.CreateBuilder ();
                        response.SetError (e.ToString ());
                    }
                    response.SetTime (GetUniversalTime ());
                    var builtResponse = response.Build ();
                    var streamResponse = request.ResponseBuilder;
                    streamResponse.SetResponse (builtResponse);
                    streamMessage.AddResponses (streamResponse);
                    rpcsExecuted++;
                }
                streamClient.Stream.Write (streamMessage.Build ());
            }

            timer.Stop ();
            RPCsExecuted += rpcsExecuted;
            TimePerStreamUpdate = (float)timer.ElapsedSeconds ();
        }

        internal uint AddStream (IClient client, Request request)
        {
            var streamClient = streamServer.Clients.Single (c => c.Guid == client.Guid);

            // Check for an existing stream for the request
            var procedure = KRPC.Service.Services.Instance.GetProcedureSignature (request);
            var arguments = KRPC.Service.Services.Instance.DecodeArguments (procedure, request);
            foreach (var streamRequest in streamRequests[streamClient]) {
                if (streamRequest.Procedure == procedure && streamRequest.Arguments.SequenceEqual (arguments))
                    return streamRequest.Identifier;
            }

            // Create a new stream
            {
                var streamRequest = new StreamRequest (request);
                streamRequests [streamClient].Add (streamRequest);
                return streamRequest.Identifier;
            }
        }

        internal void RemoveStream (IClient client, uint identifier)
        {
            var streamClient = streamServer.Clients.Single (c => c.Guid == client.Guid);
            var requests = streamRequests [streamClient].Where (x => x.Identifier == identifier).ToList ();
            if (!requests.Any ())
                return;
            streamRequests [streamClient].Remove (requests.Single ());
        }

        /// <summary>
        /// Poll connected clients for new requests.
        /// Adds a continuation to the queue for any client with a new request,
        /// if a continuation is not already being processed for the client.
        /// </summary>
        void PollRequests (IEnumerable<RequestContinuation> yieldedContinuations)
        {
            var currentClients = continuations.Select (((c) => c.Client)).ToList ();
            currentClients.AddRange (yieldedContinuations.Select (((c) => c.Client)));
            foreach (var client in clientScheduler) {
                if (!currentClients.Contains (client) && client.Stream.DataAvailable) {
                    Request request = client.Stream.Read ();
                    if (OnClientActivity != null)
                        OnClientActivity (this, new ClientActivityArgs (client));
                    if (Logger.ShouldLog (Logger.Severity.Debug))
                        Logger.WriteLine ("Received request from client " + client.Address + " (" + request.Service + "." + request.Procedure + ")", Logger.Severity.Debug);
                    continuations.Add (new RequestContinuation (client, request));
                }
            }
        }

        /// <summary>
        /// Execute the continuation and send a response to the client,
        /// or throw a YieldException if the continuation is not complete.
        /// </summary>
        void ExecuteContinuation (RequestContinuation continuation)
        {
            var client = continuation.Client;

            // Run the continuation, and either return a result, an error,
            // or throw a YieldException if the continuation has not completed
            Response.Builder response;
            try {
                Context.Set (this, client);
                response = continuation.Run ();
            } catch (YieldException) {
                throw;
            } catch (Exception e) {
                response = Response.CreateBuilder ();
                response.Error = e.Message;
                if (Logger.ShouldLog (Logger.Severity.Debug))
                    Logger.WriteLine (e.Message, Logger.Severity.Debug);
            } finally {
                Context.Clear ();
            }

            // Send response to the client
            response.SetTime (GetUniversalTime ());
            var builtResponse = response.Build ();
            ((RPCClient)client).Stream.Write (builtResponse);
            if (Logger.ShouldLog (Logger.Severity.Debug)) {
                if (response.HasError)
                    Logger.WriteLine ("Sent error response to client " + client.Address + " (" + response.Error + ")", Logger.Severity.Debug);
                else
                    Logger.WriteLine ("Sent response to client " + client.Address, Logger.Severity.Debug);
            }
        }
    }
}
