// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.Devices.Client
{
    using Common;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
#if NETSTANDARD1_3
    using System.Net.Http;
#endif
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client.Extensions;
    using Microsoft.Azure.Devices.Client.Transport;
    using Microsoft.Azure.Devices.Shared;
    using Newtonsoft.Json.Linq;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using System.Security.Cryptography.X509Certificates;

    /// <summary>
    /// Delegate for desired property update callbacks.  This will be called
    /// every time we receive a PATCH from the service.
    /// </summary>
    /// <param name="desiredProperties">Properties that were contained in the update that was received from the service</param>
    /// <param name="userContext">Context object passed in when the callback was registered</param>
    public delegate Task DesiredPropertyUpdateCallback(TwinCollection desiredProperties, object userContext);

    /// <summary>
    ///      Delegate for method call. This will be called every time we receive a method call that was registered.
    /// </summary>
    /// <param name="methodRequest">Class with details about method.</param>
    /// <param name="userContext">Context object passed in when the callback was registered.</param>
    /// <returns>MethodResponse</returns>
    public delegate Task<MethodResponse> MethodCallback(MethodRequest methodRequest, object userContext);

    /// <summary>
    /// Delegate for connection status changed.
    /// </summary>
    /// <param name="status">The updated connection status</param>
    /// <param name="reason">The reason for the connection status change</param>
    public delegate void ConnectionStatusChangesHandler(ConnectionStatus status, ConnectionStatusChangeReason reason);

    /*
     * Class Diagram and Chain of Responsibility in Device Client 
                                     +--------------------+
                                     | <<interface>>      |
                                     | IDelegatingHandler |
                                     |  * Open            |
                                     |  * Close           |
                                     |  * SendEvent       |
                                     |  * SendEvents      |
                                     |  * Receive         |
                                     |  * Complete        |
                                     |  * Abandon         |
                                     |  * Reject          |
                                     +-------+------------+
                                             |
                                             |implements
                                             |
                                             |
                                     +-------+-------+
                                     |  <<abstract>> |     
                                     |  Default      |
             +---+inherits----------->  Delegating   <------inherits-----------------+
             |                       |  Handler      |                               |
             |           +--inherits->               <--inherits----+                |
             |           |           +-------^-------+              |                |
             |           |                   |inherits              |                |
             |           |                   |                      |                |
+------------+       +---+---------+      +--+----------+       +---+--------+       +--------------+
|            |       |             |      |             |       |            |       | <<abstract>> |
| GateKeeper |  use  | Retry       | use  |  Error      |  use  | Routing    |  use  | Transport    |
| Delegating +-------> Delegating  +------>  Delegating +-------> Delegating +-------> Delegating   |
| Handler    |       | Handler     |      |  Handler    |       | Handler    |       | Handler      |
|            |       |             |      |             |       |            |       |              |
| overrides: |       | overrides:  |      |  overrides  |       | overrides: |       | overrides:   |
|  Open      |       |  Open       |      |   Open      |       |  Open      |       |  Receive     |
|  Close     |       |  SendEvent  |      |   SendEvent |       |            |       |              |
|            |       |  SendEvents |      |   SendEvents|       +------------+       +--^--^---^----+
+------------+       |  Receive    |      |   Receive   |                               |  |   |
                     |  Reject     |      |   Reject    |                               |  |   |
                     |  Abandon    |      |   Abandon   |                               |  |   |
                     |  Complete   |      |   Complete  |                               |  |   |
                     |             |      |             |                               |  |   |
                     +-------------+      +-------------+     +-------------+-+inherits-+  |   +---inherits-+-------------+
                                                              |             |              |                |             |
                                                              | AMQP        |              inherits         | HTTP        |
                                                              | Transport   |              |                | Transport   |
                                                              | Handler     |          +---+---------+      | Handler     |
                                                              |             |          |             |      |             |
                                                              | overrides:  |          | MQTT        |      | overrides:  |
                                                              |  everything |          | Transport   |      |  everything |
                                                              |             |          | Handler     |      |             |
                                                              +-------------+          |             |      +-------------+
                                                                                       | overrides:  |
                                                                                       |  everything |
                                                                                       |             |
                                                                                       +-------------+
TODO: revisit DefaultDelegatingHandler - it seems redundant as long as we have to many overloads in most of the classes.
*/

    /// <summary>
    /// Contains methods that a device can use to send messages to and receive from the service.
    /// </summary>
    public abstract class BaseClient : IDisposable
    {
        internal const string DeviceId = "DeviceId";
        internal const string DeviceIdParameterPattern = @"(^\s*?|.*;\s*?)" + DeviceId + @"\s*?=.*";

        internal IotHubConnectionString IotHubConnectionString { get; }

        /// <summary> 
        /// Diagnostic sampling percentage value, [0-100];  
        /// 0 means no message will carry on diag info 
        /// </summary>
        int _diagnosticSamplingPercentage = 0;
        public int DiagnosticSamplingPercentage
        {
            get { return _diagnosticSamplingPercentage; }
            set
            {
                if (value > 100 || value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(DiagnosticSamplingPercentage), DiagnosticSamplingPercentage,
                        "The range of diagnostic sampling percentage should between [0,100].");
                }

                if (IsE2EDiagnosticSupportedProtocol())
                {
                    _diagnosticSamplingPercentage = value;
                }
            }
        }

        ITransportSettings[] transportSettings;

        internal X509Certificate2 Certificate { get; set; }
        const RegexOptions RegexOptions = System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        internal static readonly Regex DeviceIdParameterRegex = new Regex(DeviceIdParameterPattern, RegexOptions);

        internal IDelegatingHandler InnerHandler { get; set; }

        SemaphoreSlim methodsDictionarySemaphore = new SemaphoreSlim(1, 1);

        public const string ModuleTwinsPropertyName = "moduleTwins";
        public const string MetadataName = "$metadata";

        DeviceClientConnectionStatusManager connectionStatusManager = new DeviceClientConnectionStatusManager();

        public const uint DefaultOperationTimeoutInMilliseconds = 4 * 60 * 1000;

        /// <summary>
        /// Stores the timeout used in the operation retries.
        /// </summary>
        // Codes_SRS_DEVICECLIENT_28_002: [This property shall be defaulted to 240000 (4 minutes).]
        public uint OperationTimeoutInMilliseconds { get; set; } = DefaultOperationTimeoutInMilliseconds;

        /// <summary>
        /// Stores the retry strategy used in the operation retries.
        /// </summary>
        // Codes_SRS_DEVICECLIENT_28_001: [This property shall be defaulted to the exponential retry strategy with backoff 
        // parameters for calculating delay in between retries.]
        [Obsolete("This method has been deprecated.  Please use Microsoft.Azure.Devices.Client.SetRetryPolicy(IRetryPolicy retryPolicy) instead.")]
        public RetryPolicyType RetryPolicy { get; set; }

        /// <summary>
        /// Sets the retry policy used in the operation retries.
        /// </summary>
        /// <param name="retryPolicy">The retry policy. The default is new ExponentialBackoff(int.MaxValue, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100));</param>
        // Codes_SRS_DEVICECLIENT_28_001: [This property shall be defaulted to the exponential retry strategy with backoff 
        // parameters for calculating delay in between retries.]
        public void SetRetryPolicy(IRetryPolicy retryPolicy)
        {
            var retryDelegatingHandler = GetDelegateHandler<RetryDelegatingHandler>();
            if (retryDelegatingHandler == null)
            {
                throw new NotSupportedException();
            }

            retryDelegatingHandler.SetRetryPolicy(retryPolicy);
        }


        private T GetDelegateHandler<T>() where T : DefaultDelegatingHandler
        {
            var handler = this.InnerHandler as DefaultDelegatingHandler;
            bool isFound = false;

            while (!isFound || handler == null)
            {
                if (handler is T)
                {
                    isFound = true;
                }
                else
                {
                    handler = handler.InnerHandler as DefaultDelegatingHandler;
                }
            }

            if (!isFound)
            {
                return default(T);
            }

            return (T)handler;
        }

        /// <summary>
        /// Stores Methods supported by the client device and their associated delegate.
        /// </summary>
        volatile Dictionary<string, Tuple<MethodCallback, object>> deviceMethods;

        volatile Tuple<MethodCallback, object> deviceDefaultMethodCallback;

        volatile ConnectionStatusChangesHandler connectionStatusChangesHandler;

        internal delegate Task OnMethodCalledDelegate(MethodRequestInternal methodRequestInternal);

        internal delegate Task OnConnectionClosedDelegate(object sender, ConnectionEventArgs e);

        internal delegate void OnConnectionOpenedDelegate(object sender, ConnectionEventArgs e);

        internal delegate Task OnReceiveEventMessageCalledDelegate(string input, Message message);

        /// <summary>
        /// Callback to call whenever the twin's desired state is updated by the service
        /// </summary>
        internal DesiredPropertyUpdateCallback desiredPropertyUpdateCallback;

        /// <summary>
        /// Has twin funcitonality been enabled with the service?
        /// </summary>
        Boolean patchSubscribedWithService = false;

        /// <summary>
        /// userContext passed when registering the twin patch callback
        /// </summary>
        Object twinPatchCallbackContext = null;

        private int _currentMessageCount = 0;

        internal BaseClient(IotHubConnectionString iotHubConnectionString, ITransportSettings[] transportSettings)
        {
            this.IotHubConnectionString = iotHubConnectionString;
            this.transportSettings = transportSettings;
        }

        internal static void ValidateTransportSettings(ITransportSettings[] transportSettings)
        {
            foreach (ITransportSettings transportSetting in transportSettings)
            {
                switch (transportSetting.GetTransportType())
                {
                    case TransportType.Amqp_WebSocket_Only:
                    case TransportType.Amqp_Tcp_Only:
                        if (!(transportSetting is AmqpTransportSettings))
                        {
                            throw new InvalidOperationException("Unknown implementation of ITransportSettings type");
                        }
                        break;
                    case TransportType.Http1:
                        if (!(transportSetting is Http1TransportSettings))
                        {
                            throw new InvalidOperationException("Unknown implementation of ITransportSettings type");
                        }
                        break;
                    case TransportType.Mqtt_WebSocket_Only:
                    case TransportType.Mqtt_Tcp_Only:
                        if (!(transportSetting is MqttTransportSettings))
                        {
                            throw new InvalidOperationException("Unknown implementation of ITransportSettings type");
                        }
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported Transport Type {0}".FormatInvariant(transportSetting.GetTransportType()));
                }
            }
        }

        internal static ITransportSettings[] GetTransportSettings(TransportType transportType)
        {
            switch (transportType)
            {
                case TransportType.Amqp:
                    return new ITransportSettings[]
                    {
                        new AmqpTransportSettings(TransportType.Amqp_Tcp_Only),
                        new AmqpTransportSettings(TransportType.Amqp_WebSocket_Only)
                    };

                case TransportType.Mqtt:
                    return new ITransportSettings[]
                    {
                        new MqttTransportSettings(TransportType.Mqtt_Tcp_Only),
                        new MqttTransportSettings(TransportType.Mqtt_WebSocket_Only)
                    };

                case TransportType.Amqp_WebSocket_Only:
                case TransportType.Amqp_Tcp_Only:
                    return new ITransportSettings[]
                    {
                        new AmqpTransportSettings(transportType)
                    };

                case TransportType.Mqtt_WebSocket_Only:
                case TransportType.Mqtt_Tcp_Only:
                    return new ITransportSettings[]
                    {
                        new MqttTransportSettings(transportType)
                    };

                case TransportType.Http1:
                    return new ITransportSettings[] { new Http1TransportSettings() };

                default:
                    throw new InvalidOperationException("Unsupported Transport Type {0}".FormatInvariant(transportType));
            }
        }

        internal static IDeviceClientPipelineBuilder BuildPipeline()
        {
            var transporthandlerFactory = new TransportHandlerFactory();
            IDeviceClientPipelineBuilder pipelineBuilder = new DeviceClientPipelineBuilder()
                .With(ctx => new GateKeeperDelegatingHandler(ctx))
                .With(ctx => new RetryDelegatingHandler(ctx))
                .With(ctx => new ErrorDelegatingHandler(ctx))
                .With(ctx => new ProtocolRoutingDelegatingHandler(ctx))
                .With(ctx => transporthandlerFactory.Create(ctx));
            return pipelineBuilder;
        }

        internal Task SendMethodResponseAsync(MethodResponseInternal methodResponse)
        {
            return ApplyTimeout(operationTimeoutCancellationToken =>
            {
                return this.InnerHandler.SendMethodResponseAsync(methodResponse, operationTimeoutCancellationToken);
            });
        }

        CancellationTokenSource GetOperationTimeoutCancellationTokenSource()
        {
            return new CancellationTokenSource(TimeSpan.FromMilliseconds(OperationTimeoutInMilliseconds));
        }

        /// <summary>
        /// Explicitly open the DeviceClient instance.
        /// </summary>

        public Task OpenAsync()
        {
            // Codes_SRS_DEVICECLIENT_28_007: [ The async operation shall retry until time specified in OperationTimeoutInMilliseconds property expire or unrecoverable(authentication, quota exceed) error occurs.]
            return ApplyTimeout(operationTimeoutCancellationToken => this.InnerHandler.OpenAsync(true, operationTimeoutCancellationToken));
        }

        /// <summary>
        /// Close the DeviceClient instance
        /// </summary>
        /// <returns></returns>
        public Task CloseAsync()
        {
            return this.InnerHandler.CloseAsync();
        }

        /// <summary>
        /// Sends an event to device hub
        /// </summary>
        /// <returns>The message containing the event</returns>
        public Task SendEventAsync(Message message)
        {
            if (message == null)
            {
                throw Fx.Exception.ArgumentNull("message");
            }

            IoTHubClientDiagnostic.AddDiagnosticInfoIfNecessary(message, _diagnosticSamplingPercentage, ref _currentMessageCount);
            // Codes_SRS_DEVICECLIENT_28_019: [The async operation shall retry until time specified in OperationTimeoutInMilliseconds property expire or unrecoverable error(authentication or quota exceed) occurs.]
            return ApplyTimeout(operationTimeoutCancellationToken => this.InnerHandler.SendEventAsync(message, operationTimeoutCancellationToken));
        }

        /// <summary>
        /// Sends a batch of events to device hub
        /// </summary>
        /// <returns>The task containing the event</returns>
        public Task SendEventBatchAsync(IEnumerable<Message> messages)
        {
            if (messages == null)
            {
                throw Fx.Exception.ArgumentNull("messages");
            }

            // Codes_SRS_DEVICECLIENT_28_019: [The async operation shall retry until time specified in OperationTimeoutInMilliseconds property expire or unrecoverable error(authentication or quota exceed) occurs.]
            return ApplyTimeout(operationTimeoutCancellationToken => this.InnerHandler.SendEventAsync(messages, operationTimeoutCancellationToken));
        }

        protected Task ApplyTimeout(Func<CancellationTokenSource, Task> operation)
        {
            if (OperationTimeoutInMilliseconds == 0)
            {
                var cancellationTokenSource = new CancellationTokenSource();
                return operation(cancellationTokenSource)
                    .WithTimeout(TimeSpan.MaxValue, () => Resources.OperationTimeoutExpired, cancellationTokenSource.Token);
            }

            CancellationTokenSource operationTimeoutCancellationTokenSource = GetOperationTimeoutCancellationTokenSource();

            var result = operation(operationTimeoutCancellationTokenSource)
                .WithTimeout(TimeSpan.FromMilliseconds(OperationTimeoutInMilliseconds), () => Resources.OperationTimeoutExpired, operationTimeoutCancellationTokenSource.Token);

            return result.ContinueWith(t =>
            {

                // operationTimeoutCancellationTokenSource will be disposed by GC. 
                // Cannot dispose here since we don't know if both tasks created by WithTimeout ran to completion.
                if (t.IsCanceled)
                {
                    throw new TimeoutException(Resources.OperationTimeoutExpired);
                }

                if (t.IsFaulted)
                {
                    throw t.Exception.InnerException;
                }
            });
        }

        internal Task ApplyTimeout(Func<CancellationToken, Task> operation)
        {
            if (OperationTimeoutInMilliseconds == 0)
            {
                return operation(CancellationToken.None)
                    .WithTimeout(TimeSpan.MaxValue, () => Resources.OperationTimeoutExpired, CancellationToken.None);
            }

            CancellationTokenSource operationTimeoutCancellationTokenSource = GetOperationTimeoutCancellationTokenSource();

            Debug.WriteLine(operationTimeoutCancellationTokenSource.Token.GetHashCode() + " DeviceClient.ApplyTimeout()");

            var result = operation(operationTimeoutCancellationTokenSource.Token)
                .WithTimeout(TimeSpan.FromMilliseconds(OperationTimeoutInMilliseconds), () => Resources.OperationTimeoutExpired, operationTimeoutCancellationTokenSource.Token);

            return result.ContinueWith(t =>
            {
                // operationTimeoutCancellationTokenSource will be disposed by GC. 
                // Cannot dispose here since we don't know if both tasks created by WithTimeout ran to completion.
                if (t.IsFaulted)
                {
                    throw t.Exception.InnerException;
                }
            }, TaskContinuationOptions.NotOnCanceled);
        }

        internal Task<Message> ApplyTimeoutMessage(Func<CancellationToken, Task<Message>> operation)
        {
            if (OperationTimeoutInMilliseconds == 0)
            {
                return operation(CancellationToken.None)
                    .WithTimeout(TimeSpan.MaxValue, () => Resources.OperationTimeoutExpired, CancellationToken.None);
            }

            CancellationTokenSource operationTimeoutCancellationTokenSource = GetOperationTimeoutCancellationTokenSource();

            var result = operation(operationTimeoutCancellationTokenSource.Token)
                .WithTimeout(TimeSpan.FromMilliseconds(OperationTimeoutInMilliseconds), () => Resources.OperationTimeoutExpired, operationTimeoutCancellationTokenSource.Token);

            return result.ContinueWith(t =>
            {
                // operationTimeoutCancellationTokenSource will be disposed by GC. 
                // Cannot dispose here since we don't know if both tasks created by WithTimeout ran to completion.
                if (t.IsFaulted)
                {
                    throw t.Exception.InnerException;
                }
                return t.Result;
            });
        }

        Task<Twin> ApplyTimeoutTwin(Func<CancellationToken, Task<Twin>> operation)
        {
            if (OperationTimeoutInMilliseconds == 0)
            {
                return operation(CancellationToken.None)
                    .WithTimeout(TimeSpan.MaxValue, () => Resources.OperationTimeoutExpired, CancellationToken.None);
            }

            CancellationTokenSource operationTimeoutCancellationTokenSource = GetOperationTimeoutCancellationTokenSource();

            var result = operation(operationTimeoutCancellationTokenSource.Token)
                .WithTimeout(TimeSpan.FromMilliseconds(OperationTimeoutInMilliseconds), () => Resources.OperationTimeoutExpired, operationTimeoutCancellationTokenSource.Token);

            return result.ContinueWith(t =>
            {
                // operationTimeoutCancellationTokenSource will be disposed by GC. 
                // Cannot dispose here since we don't know if both tasks created by WithTimeout ran to completion.
                if (t.IsFaulted)
                {
                    throw t.Exception.InnerException;
                }
                return t.Result;
            });
        }

        internal Task CompleteInternalAsync(string lockToken)
        {
            if (string.IsNullOrEmpty(lockToken))
            {
                throw Fx.Exception.ArgumentNull("lockToken");
            }

            // Codes_SRS_DEVICECLIENT_28_013: [The async operation shall retry until time specified in OperationTimeoutInMilliseconds property expire or unrecoverable error(authentication, quota exceed) occurs.]
            return ApplyTimeout(operationTimeoutCancellationToken => this.InnerHandler.CompleteAsync(lockToken, operationTimeoutCancellationToken.Token));
        }

        internal Task RejectInternalAsync(string lockToken)
        {
            if (string.IsNullOrEmpty(lockToken))
            {
                throw Fx.Exception.ArgumentNull("lockToken");
            }

            return ApplyTimeout(operationTimeoutCancellationToken => this.InnerHandler.RejectAsync(lockToken, operationTimeoutCancellationToken));
        }

        internal Task AbandonInternalAsync(string lockToken)
        {
            if (string.IsNullOrEmpty(lockToken))
            {
                throw Fx.Exception.ArgumentNull("lockToken");
            }
            // Codes_SRS_DEVICECLIENT_28_015: [The async operation shall retry until time specified in OperationTimeoutInMilliseconds property expire or unrecoverable error(authentication, quota exceed) occurs.]
            return ApplyTimeout(operationTimeoutCancellationToken => this.InnerHandler.AbandonAsync(lockToken, operationTimeoutCancellationToken));
        }

        /// <summary>
        /// Registers a new delegate for the named method. If a delegate is already associated with
        /// the named method, it will be replaced with the new delegate.
        /// <param name="methodName">The name of the method to associate with the delegate.</param>
        /// <param name="methodHandler">The delegate to be used when a method with the given name is called by the cloud service.</param>
        /// <param name="userContext">generic parameter to be interpreted by the client code.</param>
        /// </summary>
        public async Task SetMethodHandlerAsync(string methodName, MethodCallback methodHandler, object userContext)
        {
            try
            {
                await methodsDictionarySemaphore.WaitAsync().ConfigureAwait(false);

                if (methodHandler != null)
                {
                    // codes_SRS_DEVICECLIENT_10_005: [ It shall EnableMethodsAsync when called for the first time. ]
                    await this.EnableMethodAsync().ConfigureAwait(false);

                    // codes_SRS_DEVICECLIENT_10_001: [ It shall lazy-initialize the deviceMethods property. ]
                    if (this.deviceMethods == null)
                    {
                        this.deviceMethods = new Dictionary<string, Tuple<MethodCallback, object>>();
                    }
                    this.deviceMethods[methodName] = new Tuple<MethodCallback, object>(methodHandler, userContext);
                }
                else
                {
                    // codes_SRS_DEVICECLIENT_10_002: [ If the given methodName already has an associated delegate, the existing delegate shall be removed. ]
                    // codes_SRS_DEVICECLIENT_10_003: [ The given delegate will only be added if it is not null. ]
                    if (this.deviceMethods != null)
                    {
                        this.deviceMethods.Remove(methodName);

                        if (this.deviceMethods.Count == 0)
                        {
                            // codes_SRS_DEVICECLIENT_10_004: [ The deviceMethods property shall be deleted if the last delegate has been removed. ]
                            this.deviceMethods = null;
                        }

                        // codes_SRS_DEVICECLIENT_10_006: [ It shall DisableMethodsAsync when the last delegate has been removed. ]
                        await this.DisableMethodAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                methodsDictionarySemaphore.Release();
            }
        }

        /// <summary>
        /// Registers a new delegate that is called for a method that doesn't have a delegate registered for its name. 
        /// If a default delegate is already registered it will replace with the new delegate.
        /// </summary>
        /// <param name="methodHandler">The delegate to be used when a method is called by the cloud service and there is no delegate registered for that method name.</param>
        /// <param name="userContext">Generic parameter to be interpreted by the client code.</param>
        public async Task SetMethodDefaultHandlerAsync(MethodCallback methodHandler, object userContext)
        {
            try
            {
                await methodsDictionarySemaphore.WaitAsync().ConfigureAwait(false);
                if (methodHandler != null)
                {
                    // codes_SRS_DEVICECLIENT_10_005: [ It shall EnableMethodsAsync when called for the first time. ]
                    await this.EnableMethodAsync().ConfigureAwait(false);

                    // codes_SRS_DEVICECLIENT_24_001: [ If the default callback has already been set, it is replaced with the new callback. ]
                    this.deviceDefaultMethodCallback = new Tuple<MethodCallback, object>(methodHandler, userContext);
                }
                else
                {
                    this.deviceDefaultMethodCallback = null;

                    // codes_SRS_DEVICECLIENT_10_006: [ It shall DisableMethodsAsync when the last delegate has been removed. ]
                    await this.DisableMethodAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                methodsDictionarySemaphore.Release();
            }
        }

        /// <summary>
        /// Registers a new delegate for the named method. If a delegate is already associated with
        /// the named method, it will be replaced with the new delegate.
        /// <param name="methodName">The name of the method to associate with the delegate.</param>
        /// <param name="methodHandler">The delegate to be used when a method with the given name is called by the cloud service.</param>
        /// <param name="userContext">generic parameter to be interpreted by the client code.</param>
        /// </summary>

        [Obsolete("Please use SetMethodHandlerAsync.")]
        public void SetMethodHandler(string methodName, MethodCallback methodHandler, object userContext)
        {
            methodsDictionarySemaphore.Wait();

            try
            {
                if (methodHandler != null)
                {
                    // codes_SRS_DEVICECLIENT_10_001: [ It shall lazy-initialize the deviceMethods property. ]
                    if (this.deviceMethods == null)
                    {
                        this.deviceMethods = new Dictionary<string, Tuple<MethodCallback, object>>();

                        // codes_SRS_DEVICECLIENT_10_005: [ It shall EnableMethodsAsync when called for the first time. ]
                        ApplyTimeout(operationTimeoutCancellationToken => this.InnerHandler.EnableMethodsAsync(operationTimeoutCancellationToken)).Wait();
                    }
                    this.deviceMethods[methodName] = new Tuple<MethodCallback, object>(methodHandler, userContext);
                }
                else
                {
                    // codes_SRS_DEVICECLIENT_10_002: [ If the given methodName already has an associated delegate, the existing delegate shall be removed. ]
                    // codes_SRS_DEVICECLIENT_10_003: [ The given delegate will only be added if it is not null. ]
                    if (this.deviceMethods != null)
                    {
                        this.deviceMethods.Remove(methodName);

                        if (this.deviceMethods.Count == 0)
                        {
                            // codes_SRS_DEVICECLIENT_10_006: [ It shall DisableMethodsAsync when the last delegate has been removed. ]
                            ApplyTimeout(operationTimeoutCancellationToken => this.InnerHandler.DisableMethodsAsync(operationTimeoutCancellationToken)).Wait();

                            // codes_SRS_DEVICECLIENT_10_004: [ The deviceMethods property shall be deleted if the last delegate has been removed. ]
                            this.deviceMethods = null;
                        }
                    }
                }
            }
            finally
            {
                methodsDictionarySemaphore.Release();
            }
        }

        /// <summary>
        /// Registers a new delegate for the connection status changed callback. If a delegate is already associated, 
        /// it will be replaced with the new delegate.
        /// <param name="statusChangesHandler">The name of the method to associate with the delegate.</param>
        /// </summary>
        public void SetConnectionStatusChangesHandler(ConnectionStatusChangesHandler statusChangesHandler)
        {
            // codes_SRS_DEVICECLIENT_28_025: [** `SetConnectionStatusChangesHandler` shall set connectionStatusChangesHandler **]**
            // codes_SRS_DEVICECLIENT_28_026: [** `SetConnectionStatusChangesHandler` shall unset connectionStatusChangesHandler if `statusChangesHandler` is null **]**
            this.connectionStatusChangesHandler = statusChangesHandler;
        }

        /// <summary>
        /// The delegate for handling disrupted connection/links in the transport layer.
        /// </summary>
        internal void OnConnectionOpened(object sender, ConnectionEventArgs e)
        {
            ConnectionStatusChangeResult result = this.connectionStatusManager.ChangeTo(e.ConnectionType, ConnectionStatus.Connected);
            if (result.IsClientStatusChanged && (connectionStatusChangesHandler != null))
            {
                // codes_SRS_DEVICECLIENT_28_024: [** `OnConnectionOpened` shall invoke the connectionStatusChangesHandler if ConnectionStatus is changed **]**  
                this.connectionStatusChangesHandler(ConnectionStatus.Connected, e.ConnectionStatusChangeReason);
            }
        }

        /// <summary>
        /// The delegate for handling disrupted connection/links in the transport layer.
        /// </summary>
        internal async Task OnConnectionClosed(object sender, ConnectionEventArgs e)
        {
            ConnectionStatusChangeResult result = null;

            // codes_SRS_DEVICECLIENT_28_023: [** `OnConnectionClosed` shall notify ConnectionStatusManager of the connection updates. **]**
            if (e.ConnectionStatus == ConnectionStatus.Disconnected_Retrying)
            {
                try
                {
                    // codes_SRS_DEVICECLIENT_28_022: [** `OnConnectionClosed` shall invoke the RecoverConnections operation. **]**          
                    await ApplyTimeout(async operationTimeoutCancellationTokenSource =>
                    {
                        result = this.connectionStatusManager.ChangeTo(e.ConnectionType, ConnectionStatus.Disconnected_Retrying, ConnectionStatus.Connected);
                        if (result.IsClientStatusChanged && (connectionStatusChangesHandler != null))
                        {
                            this.connectionStatusChangesHandler(e.ConnectionStatus, e.ConnectionStatusChangeReason);
                        }

                        using (CancellationTokenSource linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(result.StatusChangeCancellationTokenSource.Token, operationTimeoutCancellationTokenSource.Token))
                        {
                            await this.InnerHandler.RecoverConnections(sender, e.ConnectionType, linkedTokenSource.Token).ConfigureAwait(false);
                        }

                    }).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // codes_SRS_DEVICECLIENT_28_027: [** `OnConnectionClosed` shall invoke the connectionStatusChangesHandler if RecoverConnections throw exception **]**
                    result = this.connectionStatusManager.ChangeTo(e.ConnectionType, ConnectionStatus.Disconnected);
                    if (result.IsClientStatusChanged && (connectionStatusChangesHandler != null))
                    {
                        this.connectionStatusChangesHandler(ConnectionStatus.Disconnected, ConnectionStatusChangeReason.Retry_Expired);
                    }
                }
            }
            else
            {
                result = this.connectionStatusManager.ChangeTo(e.ConnectionType, ConnectionStatus.Disabled);
                if (result.IsClientStatusChanged && (connectionStatusChangesHandler != null))
                {
                    this.connectionStatusChangesHandler(ConnectionStatus.Disabled, e.ConnectionStatusChangeReason);
                }
            }
        }

        /// <summary>
        /// The delegate for handling direct methods received from service.
        /// </summary>
        internal async Task OnMethodCalled(MethodRequestInternal methodRequestInternal)
        {
            Tuple<MethodCallback, object> m = null;

            // codes_SRS_DEVICECLIENT_10_012: [ If the given methodRequestInternal argument is null, fail silently ]
            if (methodRequestInternal != null)
            {
                MethodResponseInternal methodResponseInternal;
                byte[] requestData = methodRequestInternal.GetBytes();

                await methodsDictionarySemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    Utils.ValidateDataIsEmptyOrJson(requestData);
                    this.deviceMethods?.TryGetValue(methodRequestInternal.Name, out m);
                    if (m == null)
                    {
                        m = this.deviceDefaultMethodCallback;
                    }
                }
                catch (Exception)
                {
                    // codes_SRS_DEVICECLIENT_28_020: [ If the given methodRequestInternal data is not valid json, respond with status code 400 (BAD REQUEST) ]
                    methodResponseInternal = new MethodResponseInternal(methodRequestInternal.RequestId, (int)MethodResposeStatusCode.BadRequest);
                    await this.SendMethodResponseAsync(methodResponseInternal).ConfigureAwait(false);
                    return;
                }
                finally
                {
                    methodsDictionarySemaphore.Release();
                }

                if (m != null)
                {
                    try
                    {
                        // codes_SRS_DEVICECLIENT_10_011: [ The OnMethodCalled shall invoke the specified delegate. ]
                        // codes_SRS_DEVICECLIENT_24_002: [ The OnMethodCalled shall invoke the default delegate if there is no specified delegate for that method. ]
                        MethodResponse rv = await m.Item1(new MethodRequest(methodRequestInternal.Name, requestData), m.Item2).ConfigureAwait(false);

                        // codes_SRS_DEVICECLIENT_03_012: [If the MethodResponse does not contain result, the MethodResponseInternal constructor shall be invoked with no results.]
                        if (rv.Result == null)
                        {
                            methodResponseInternal = new MethodResponseInternal(methodRequestInternal.RequestId, rv.Status);
                        }
                        // codes_SRS_DEVICECLIENT_03_013: [Otherwise, the MethodResponseInternal constructor shall be invoked with the result supplied.]
                        else
                        {
                            methodResponseInternal = new MethodResponseInternal(rv.Result, methodRequestInternal.RequestId, rv.Status);
                        }
                    }
                    catch (Exception)
                    {
                        // codes_SRS_DEVICECLIENT_28_021: [ If the MethodResponse from the MethodHandler is not valid json, respond with status code 500 (USER CODE EXCEPTION) ]
                        methodResponseInternal = new MethodResponseInternal(methodRequestInternal.RequestId, (int)MethodResposeStatusCode.UserCodeException);
                    }
                }
                else
                {
                    // codes_SRS_DEVICECLIENT_10_013: [ If the given method does not have an associated delegate and no default delegate was registered, respond with status code 501 (METHOD NOT IMPLEMENTED) ]
                    methodResponseInternal = new MethodResponseInternal(methodRequestInternal.RequestId, (int)MethodResposeStatusCode.MethodNotImplemented);
                }
                await this.SendMethodResponseAsync(methodResponseInternal).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            this.InnerHandler?.Dispose();
        }

        internal static ITransportSettings[] PopulateCertificateInTransportSettings(IotHubConnectionStringBuilder connectionStringBuilder, TransportType transportType)
        {
            switch (transportType)
            {
                case TransportType.Amqp:
                    return new ITransportSettings[]
                    {
                        new AmqpTransportSettings(TransportType.Amqp_Tcp_Only)
                        {
                            ClientCertificate = connectionStringBuilder.Certificate
                        },
                        new AmqpTransportSettings(TransportType.Amqp_WebSocket_Only)
                        {
                            ClientCertificate = connectionStringBuilder.Certificate
                        }
                    };
                case TransportType.Amqp_Tcp_Only:
                    return new ITransportSettings[]
                    {
                        new AmqpTransportSettings(TransportType.Amqp_Tcp_Only)
                        {
                            ClientCertificate = connectionStringBuilder.Certificate
                        }
                    };
                case TransportType.Amqp_WebSocket_Only:
                    return new ITransportSettings[]
                    {
                        new AmqpTransportSettings(TransportType.Amqp_WebSocket_Only)
                        {
                            ClientCertificate = connectionStringBuilder.Certificate
                        }
                    };
                case TransportType.Http1:
                    return new ITransportSettings[]
                    {
                        new Http1TransportSettings()
                        {
                            ClientCertificate = connectionStringBuilder.Certificate
                        }
                    };
                case TransportType.Mqtt:
                    return new ITransportSettings[]
                    {
                        new MqttTransportSettings(TransportType.Mqtt_Tcp_Only)
                        {
                            ClientCertificate = connectionStringBuilder.Certificate
                        },
                        new MqttTransportSettings(TransportType.Mqtt_WebSocket_Only)
                        {
                            ClientCertificate = connectionStringBuilder.Certificate
                        }
                    };
                case TransportType.Mqtt_Tcp_Only:
                    return new ITransportSettings[]
                    {
                        new MqttTransportSettings(TransportType.Mqtt_Tcp_Only)
                        {
                            ClientCertificate = connectionStringBuilder.Certificate
                        }
                    };
                case TransportType.Mqtt_WebSocket_Only:
                    return new ITransportSettings[]
                    {
                        new MqttTransportSettings(TransportType.Mqtt_WebSocket_Only)
                        {
                            ClientCertificate = connectionStringBuilder.Certificate
                        }
                    };
                default:
                    throw new InvalidOperationException("Unsupported Transport {0}".FormatInvariant(transportType));
            }
        }

        internal static ITransportSettings[] PopulateCertificateInTransportSettings(IotHubConnectionStringBuilder connectionStringBuilder, ITransportSettings[] transportSettings)
        {
            foreach (var transportSetting in transportSettings)
            {
                switch (transportSetting.GetTransportType())
                {
                    case TransportType.Amqp_WebSocket_Only:
                    case TransportType.Amqp_Tcp_Only:
                        ((AmqpTransportSettings)transportSetting).ClientCertificate = connectionStringBuilder.Certificate;
                        break;
                    case TransportType.Http1:
                        ((Http1TransportSettings)transportSetting).ClientCertificate = connectionStringBuilder.Certificate;
                        break;
                    case TransportType.Mqtt_WebSocket_Only:
                    case TransportType.Mqtt_Tcp_Only:
                        ((MqttTransportSettings)transportSetting).ClientCertificate = connectionStringBuilder.Certificate;
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported Transport {0}".FormatInvariant(transportSetting.GetTransportType()));
                }
            }

            return transportSettings;
        }

        /// <summary>
        /// Set a callback that will be called whenever the client receives a state update 
        /// (desired or reported) from the service.  This has the side-effect of subscribing
        /// to the PATCH topic on the service.
        /// </summary>
        /// <param name="callback">Callback to call after the state update has been received and applied</param>
        /// <param name="userContext">Context object that will be passed into callback</param>
        [Obsolete("Please use SetDesiredPropertyUpdateCallbackAsync.")]
        public Task SetDesiredPropertyUpdateCallback(DesiredPropertyUpdateCallback callback, object userContext)
        {
            return SetDesiredPropertyUpdateCallbackAsync(callback, userContext);
        }

        /// <summary>
        /// Set a callback that will be called whenever the client receives a state update 
        /// (desired or reported) from the service.  This has the side-effect of subscribing
        /// to the PATCH topic on the service.
        /// </summary>
        /// <param name="callback">Callback to call after the state update has been received and applied</param>
        /// <param name="userContext">Context object that will be passed into callback</param>
        public Task SetDesiredPropertyUpdateCallbackAsync(DesiredPropertyUpdateCallback callback, object userContext)
        {
            // Codes_SRS_DEVICECLIENT_18_007: `SetDesiredPropertyUpdateCallbackAsync` shall throw an `ArgumentNull` exception if `callback` is null
            if (callback == null)
            {
                throw Fx.Exception.ArgumentNull("callback");
            }

            return ApplyTimeout(async operationTimeoutCancellationToken =>
            {
                // Codes_SRS_DEVICECLIENT_18_003: `SetDesiredPropertyUpdateCallbackAsync` shall call the transport to register for PATCHes on it's first call.
                // Codes_SRS_DEVICECLIENT_18_004: `SetDesiredPropertyUpdateCallbackAsync` shall not call the transport to register for PATCHes on subsequent calls
                if (!this.patchSubscribedWithService)
                {
                    await this.InnerHandler.EnableTwinPatchAsync(operationTimeoutCancellationToken).ConfigureAwait(false);
                    patchSubscribedWithService = true;
                }

                this.desiredPropertyUpdateCallback = callback;
                this.twinPatchCallbackContext = userContext;
            });
        }

        /// <summary>
        /// Retrieve a device twin object for the current device.
        /// </summary>
        /// <returns>The device twin object for the current device</returns>
        public Task<Twin> GetTwinAsync()
        {
            return ApplyTimeoutTwin(async operationTimeoutCancellationToken =>
            {
                // Codes_SRS_DEVICECLIENT_18_001: `GetTwinAsync` shall call `SendTwinGetAsync` on the transport to get the twin state
                return await this.InnerHandler.SendTwinGetAsync(operationTimeoutCancellationToken).ConfigureAwait(false);
            });
        }

        /// <summary>
        /// Push reported property changes up to the service.
        /// </summary>
        /// <param name="reportedProperties">Reported properties to push</param>
        public Task UpdateReportedPropertiesAsync(TwinCollection reportedProperties)
        {
            // Codes_SRS_DEVICECLIENT_18_006: `UpdateReportedPropertiesAsync` shall throw an `ArgumentNull` exception if `reportedProperties` is null
            if (reportedProperties == null)
            {
                throw Fx.Exception.ArgumentNull("reportedProperties");
            }
            return ApplyTimeout(async operationTimeoutCancellationToken =>
            {
                // Codes_SRS_DEVICECLIENT_18_002: `UpdateReportedPropertiesAsync` shall call `SendTwinPatchAsync` on the transport to update the reported properties
                await this.InnerHandler.SendTwinPatchAsync(reportedProperties, operationTimeoutCancellationToken).ConfigureAwait(false);
            });
        }

        //  Codes_SRS_DEVICECLIENT_18_005: When a patch is received from the service, the `callback` shall be called.
        internal void OnReportedStatePatchReceived(TwinCollection patch)
        {
            if (this.desiredPropertyUpdateCallback != null)
            {
                this.desiredPropertyUpdateCallback(patch, this.twinPatchCallbackContext);
            }
        }

        private async Task EnableMethodAsync()
        {
            if (this.deviceMethods == null && this.deviceDefaultMethodCallback == null)
            {
                await ApplyTimeout(operationTimeoutCancellationToken => this.InnerHandler.EnableMethodsAsync(operationTimeoutCancellationToken)).ConfigureAwait(false);
            }
        }

        private async Task DisableMethodAsync()
        {
            if (this.deviceMethods == null && this.deviceDefaultMethodCallback == null)
            {
                await ApplyTimeout(operationTimeoutCancellationToken => this.InnerHandler.DisableMethodsAsync(operationTimeoutCancellationToken)).ConfigureAwait(false);
            }
        }

        private bool IsE2EDiagnosticSupportedProtocol()
        {
            foreach (ITransportSettings transportSetting in this.transportSettings)
            {
                var transportType = transportSetting.GetTransportType();
                if (!(transportType == TransportType.Amqp_WebSocket_Only || transportType == TransportType.Amqp_Tcp_Only
                    || transportType == TransportType.Mqtt_WebSocket_Only || transportType == TransportType.Mqtt_Tcp_Only))
                {
                    throw new NotSupportedException($"{transportType} protocal doesn't support E2E diagnostic.");
                }
            }
            return true;
        }
    }
}