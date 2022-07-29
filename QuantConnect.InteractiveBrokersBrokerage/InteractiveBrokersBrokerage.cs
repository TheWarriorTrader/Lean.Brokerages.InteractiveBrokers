/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using IBApi;
using NodaTime;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.IBAutomater;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Securities.FutureOption;
using QuantConnect.Securities.Index;
using QuantConnect.Securities.IndexOption;
using QuantConnect.Securities.Option;
using QuantConnect.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Api;
using RestSharp;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Bar = QuantConnect.Data.Market.Bar;
using HistoryRequest = QuantConnect.Data.HistoryRequest;
using IB = QuantConnect.Brokerages.InteractiveBrokers.Client;
using Order = QuantConnect.Orders.Order;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Brokerages.InteractiveBrokers
{
    /// <summary>
    /// The Interactive Brokers brokerage
    /// </summary>
    [BrokerageFactory(typeof(InteractiveBrokersBrokerageFactory))]
    public sealed class InteractiveBrokersBrokerage : Brokerage, IDataQueueHandler, IDataQueueUniverseProvider
    {
        /// <summary>
        /// During market open there can be some extra delay and resource constraint so let's be generous
        /// </summary>
        private static readonly TimeSpan _responseTimeout = TimeSpan.FromSeconds(Config.GetInt("ib-response-timeout", 60 * 5));

        /// <summary>
        /// The default gateway version to use
        /// </summary>
        public static string DefaultVersion { get; } = "1012";

        private IBAutomater.IBAutomater _ibAutomater;

        // Existing orders created in TWS can *only* be cancelled/modified when connected with ClientId = 0
        private const int ClientId = 0;

        private const string _futuresCmeCrypto = "CMECRYPTO";

        // next valid order id (or request id, or ticker id) for this client
        private int _nextValidId;

        private readonly object _nextValidIdLocker = new object();

        private int _port;
        private string _account;
        private string _host;
        private IAlgorithm _algorithm;
        private bool _loadExistingHoldings;
        private IOrderProvider _orderProvider;
        private ISecurityProvider _securityProvider;
        private IDataAggregator _aggregator;
        private IB.InteractiveBrokersClient _client;
        private int _ibVersion;
        private string _agentDescription;
        private EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;

        private Thread _messageProcessingThread;
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private ManualResetEvent _currentTimeEvent = new ManualResetEvent(false);
        private Thread _heartBeatThread;

        // Notifies the thread reading information from Gateway/TWS whenever there are messages ready to be consumed
        private readonly EReaderSignal _signal = new EReaderMonitorSignal();

        private readonly ManualResetEvent _connectEvent = new ManualResetEvent(false);
        private readonly ManualResetEvent _waitForNextValidId = new ManualResetEvent(false);
        private readonly ManualResetEvent _accountHoldingsResetEvent = new ManualResetEvent(false);
        private Exception _accountHoldingsLastException;

        // tracks pending brokerage order responses. In some cases we've seen orders been placed and they never get through to IB
        private readonly ConcurrentDictionary<int, ManualResetEventSlim> _pendingOrderResponse = new();

        // tracks requested order updates, so we can flag Submitted order events as updates
        private readonly ConcurrentDictionary<int, int> _orderUpdates = new ConcurrentDictionary<int, int>();

        // tracks executions before commission reports, map: execId -> execution
        private readonly ConcurrentDictionary<string, IB.ExecutionDetailsEventArgs> _orderExecutions = new ConcurrentDictionary<string, IB.ExecutionDetailsEventArgs>();

        // tracks commission reports before executions, map: execId -> commission report
        private readonly ConcurrentDictionary<string, CommissionReport> _commissionReports = new ConcurrentDictionary<string, CommissionReport>();

        // holds account properties, cash balances and holdings for the account
        private readonly InteractiveBrokersAccountData _accountData = new InteractiveBrokersAccountData();

        // holds brokerage state information (connection status, error conditions, etc.)
        private readonly InteractiveBrokersStateManager _stateManager = new InteractiveBrokersStateManager();

        private readonly object _sync = new object();

        private readonly ConcurrentDictionary<string, ContractDetails> _contractDetails = new ConcurrentDictionary<string, ContractDetails>();

        private InteractiveBrokersSymbolMapper _symbolMapper;

        // Prioritized list of exchanges used to find right futures contract
        private readonly Dictionary<string, string> _futuresExchanges = new Dictionary<string, string>
        {
            { Market.CME, "GLOBEX" },
            { Market.NYMEX, "NYMEX" },
            { Market.COMEX, "NYMEX" },
            { Market.CBOT, "ECBOT" },
            { Market.ICE, "NYBOT" },
            { Market.CFE, "CFE" },
            { Market.NYSELIFFE, "NYSELIFFE" }
        };

        private readonly SymbolPropertiesDatabase _symbolPropertiesDatabase = SymbolPropertiesDatabase.FromDataFolder();

        // exchange time zones by symbol
        private readonly Dictionary<Symbol, DateTimeZone> _symbolExchangeTimeZones = new Dictionary<Symbol, DateTimeZone>();

        // IB requests made through the IB-API must be limited to a maximum of 50 messages/second
        private readonly RateGate _messagingRateLimiter = new RateGate(50, TimeSpan.FromSeconds(1));

        // additional IB request information, will be matched with errors in the handler, for better error reporting
        private readonly ConcurrentDictionary<int, string> _requestInformation = new ConcurrentDictionary<int, string>();

        // when unsubscribing symbols immediately after subscribing IB returns an error (Can't find EId with tickerId:nnn),
        // so we track subscription times to ensure symbols are not unsubscribed before a minimum time span has elapsed
        private readonly Dictionary<int, DateTime> _subscriptionTimes = new Dictionary<int, DateTime>();

        private readonly TimeSpan _minimumTimespanBeforeUnsubscribe = TimeSpan.FromMilliseconds(500);

        private readonly bool _enableDelayedStreamingData = Config.GetBool("ib-enable-delayed-streaming-data");

        private volatile bool _isDisposeCalled;
        private bool _isInitialized;

        /// <summary>
        /// Returns true if we're currently connected to the broker
        /// </summary>
        public override bool IsConnected => _client != null && _client.Connected && !_stateManager.Disconnected1100Fired;

        /// <summary>
        /// Returns true if the connected user is a financial advisor
        /// </summary>
        public bool IsFinancialAdvisor => IsMasterAccount(_account);

        /// <summary>
        /// Returns true if the account is a financial advisor master account
        /// </summary>
        /// <param name="account">The account code</param>
        /// <returns>True if the account is a master account</returns>
        public static bool IsMasterAccount(string account)
        {
            return account.Contains("F");
        }

        /// <summary>
        /// Creates a new InteractiveBrokersBrokerage using values from configuration
        /// </summary>
        public InteractiveBrokersBrokerage() : base("Interactive Brokers Brokerage")
        {
        }

        /// <summary>
        /// Creates a new InteractiveBrokersBrokerage using values from configuration:
        ///     ib-account (required)
        ///     ib-host (optional, defaults to LOCALHOST)
        ///     ib-port (optional, defaults to 4001)
        ///     ib-agent-description (optional, defaults to Individual)
        /// </summary>
        /// <param name="algorithm">The algorithm instance</param>
        /// <param name="orderProvider">An instance of IOrderProvider used to fetch Order objects by brokerage ID</param>
        /// <param name="securityProvider">The security provider used to give access to algorithm securities</param>
        /// <param name="aggregator">consolidate ticks</param>
        /// <param name="mapFileProvider">representing all the map files</param>
        public InteractiveBrokersBrokerage(IAlgorithm algorithm, IOrderProvider orderProvider, ISecurityProvider securityProvider, IDataAggregator aggregator, IMapFileProvider mapFileProvider)
            : this(algorithm, orderProvider, securityProvider, aggregator, mapFileProvider, Config.Get("ib-account"))
        {
        }

        /// <summary>
        /// Creates a new InteractiveBrokersBrokerage for the specified account
        /// </summary>
        /// <param name="algorithm">The algorithm instance</param>
        /// <param name="orderProvider">An instance of IOrderProvider used to fetch Order objects by brokerage ID</param>
        /// <param name="securityProvider">The security provider used to give access to algorithm securities</param>
        /// <param name="aggregator">consolidate ticks</param>
        /// <param name="mapFileProvider">representing all the map files</param>
        /// <param name="account">The account used to connect to IB</param>
        public InteractiveBrokersBrokerage(IAlgorithm algorithm, IOrderProvider orderProvider, ISecurityProvider securityProvider, IDataAggregator aggregator, IMapFileProvider mapFileProvider, string account)
            : this(
                algorithm,
                orderProvider,
                securityProvider,
                aggregator,
                mapFileProvider,
                account,
                Config.Get("ib-host", "LOCALHOST"),
                Config.GetInt("ib-port", 4001),
                Config.Get("ib-tws-dir"),
                Config.Get("ib-version", DefaultVersion),
                Config.Get("ib-user-name"),
                Config.Get("ib-password"),
                Config.Get("ib-trading-mode"),
                Config.GetValue("ib-agent-description", IB.AgentDescription.Individual)
                )
        {
        }

        /// <summary>
        /// Creates a new InteractiveBrokersBrokerage from the specified values
        /// </summary>
        /// <param name="algorithm">The algorithm instance</param>
        /// <param name="orderProvider">An instance of IOrderProvider used to fetch Order objects by brokerage ID</param>
        /// <param name="securityProvider">The security provider used to give access to algorithm securities</param>
        /// <param name="aggregator">consolidate ticks</param>
        /// <param name="mapFileProvider">representing all the map files</param>
        /// <param name="account">The Interactive Brokers account name</param>
        /// <param name="host">host name or IP address of the machine where TWS is running. Leave blank to connect to the local host.</param>
        /// <param name="port">must match the port specified in TWS on the Configure&gt;API&gt;Socket Port field.</param>
        /// <param name="ibDirectory">The IB Gateway root directory</param>
        /// <param name="ibVersion">The IB Gateway version</param>
        /// <param name="userName">The login user name</param>
        /// <param name="password">The login password</param>
        /// <param name="tradingMode">The trading mode: 'live' or 'paper'</param>
        /// <param name="agentDescription">Used for Rule 80A describes the type of trader.</param>
        /// <param name="loadExistingHoldings">False will ignore existing security holdings from being loaded.</param>
        public InteractiveBrokersBrokerage(
            IAlgorithm algorithm,
            IOrderProvider orderProvider,
            ISecurityProvider securityProvider,
            IDataAggregator aggregator,
            IMapFileProvider mapFileProvider,
            string account,
            string host,
            int port,
            string ibDirectory,
            string ibVersion,
            string userName,
            string password,
            string tradingMode,
            string agentDescription = IB.AgentDescription.Individual,
            bool loadExistingHoldings = true)
            : base("Interactive Brokers Brokerage")
        {
            Initialize(
                algorithm,
                orderProvider,
                securityProvider,
                aggregator,
                mapFileProvider,
                account,
                host,
                port,
                ibDirectory,
                ibVersion,
                userName,
                password,
                tradingMode,
                agentDescription = IB.AgentDescription.Individual,
                loadExistingHoldings = true);
        }

        /// <summary>
        /// Provides public access to the underlying IBClient instance
        /// </summary>
        public IB.InteractiveBrokersClient Client => _client;

        /// <summary>
        /// Places a new order and assigns a new broker ID to the order
        /// </summary>
        /// <param name="order">The order to be placed</param>
        /// <returns>True if the request for a new order has been placed, false otherwise</returns>
        public override bool PlaceOrder(Order order)
        {
            try
            {
                Log.Trace($"InteractiveBrokersBrokerage.PlaceOrder(): Symbol: {order.Symbol.Value} Quantity: {order.Quantity}. Id: {order.Id}");

                if (!IsConnected)
                {
                    OnMessage(
                        new BrokerageMessageEvent(
                            BrokerageMessageType.Warning,
                            "PlaceOrderWhenDisconnected",
                            "Orders cannot be submitted when disconnected."));
                    return false;
                }

                IBPlaceOrder(order, true);
                return true;
            }
            catch (Exception err)
            {
                Log.Error("InteractiveBrokersBrokerage.PlaceOrder(): " + err);
                return false;
            }
        }

        /// <summary>
        /// Updates the order with the same id
        /// </summary>
        /// <param name="order">The new order information</param>
        /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
        public override bool UpdateOrder(Order order)
        {
            try
            {
                Log.Trace($"InteractiveBrokersBrokerage.UpdateOrder(): Symbol: {order.Symbol.Value} Quantity: {order.Quantity} Status: {order.Status} Id: {order.Id}");

                if (!IsConnected)
                {
                    OnMessage(
                        new BrokerageMessageEvent(
                            BrokerageMessageType.Warning,
                            "UpdateOrderWhenDisconnected",
                            "Orders cannot be updated when disconnected."));
                    return false;
                }

                _orderUpdates[order.Id] = order.Id;
                IBPlaceOrder(order, false);
            }
            catch (Exception err)
            {
                int id;
                _orderUpdates.TryRemove(order.Id, out id);
                Log.Error("InteractiveBrokersBrokerage.UpdateOrder(): " + err);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was made for the order to be canceled, false otherwise</returns>
        public override bool CancelOrder(Order order)
        {
            try
            {
                Log.Trace("InteractiveBrokersBrokerage.CancelOrder(): Symbol: " + order.Symbol.Value + " Quantity: " + order.Quantity);

                if (!IsConnected)
                {
                    OnMessage(
                        new BrokerageMessageEvent(
                            BrokerageMessageType.Warning,
                            "CancelOrderWhenDisconnected",
                            "Orders cannot be cancelled when disconnected."));
                    return false;
                }

                // this could be better
                foreach (var id in order.BrokerId)
                {
                    var orderId = Parse.Int(id);

                    _requestInformation[orderId] = $"[Id={orderId}] CancelOrder: " + order;

                    CheckRateLimiting();

                    var eventSlim = new ManualResetEventSlim(false);
                    _pendingOrderResponse[orderId] = eventSlim;

                    _client.ClientSocket.cancelOrder(orderId);

                    if (!eventSlim.Wait(_responseTimeout))
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "NoBrokerageResponse", $"Timeout waiting for brokerage response for brokerage order id {orderId} lean id {order.Id}"));
                    }
                    else
                    {
                        eventSlim.DisposeSafely();
                    }
                }

                // canceled order events fired upon confirmation, see HandleError
            }
            catch (Exception err)
            {
                Log.Error("InteractiveBrokersBrokerage.CancelOrder(): OrderID: " + order.Id + " - " + err);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Gets all open orders on the account
        /// </summary>
        /// <returns>The open orders returned from IB</returns>
        public override List<Order> GetOpenOrders()
        {
            // If client 0 invokes reqOpenOrders, it will cause currently open orders placed from TWS manually to be 'bound',
            // i.e. assigned an order ID so that they can be modified or cancelled by the API client 0.
            GetOpenOrdersInternal(false);

            // return all open orders (including those placed from TWS, which will have a negative order id)
            lock (_nextValidIdLocker)
            {
                return GetOpenOrdersInternal(true);
            }
        }

        private List<Order> GetOpenOrdersInternal(bool all)
        {
            var orders = new List<(IBApi.Order Order, Contract Contract)>();

            var manualResetEvent = new ManualResetEvent(false);

            Exception exception = null;
            var lastOrderId = 0;

            // define our handlers
            EventHandler<IB.OpenOrderEventArgs> clientOnOpenOrder = (sender, args) =>
            {
                try
                {
                    if (args.OrderId > lastOrderId)
                    {
                        lastOrderId = args.OrderId;
                    }

                    // keep the IB order and contract objects returned from RequestOpenOrders
                    orders.Add((args.Order, args.Contract));
                }
                catch (Exception e)
                {
                    exception = e;
                }
            };
            EventHandler clientOnOpenOrderEnd = (sender, args) =>
            {
                // this signals the end of our RequestOpenOrders call
                manualResetEvent.Set();
            };

            _client.OpenOrder += clientOnOpenOrder;
            _client.OpenOrderEnd += clientOnOpenOrderEnd;

            CheckRateLimiting();

            if (all)
            {
                _client.ClientSocket.reqAllOpenOrders();
            }
            else
            {
                _client.ClientSocket.reqOpenOrders();
            }

            // wait for our end signal
            var timedOut = !manualResetEvent.WaitOne(15000);

            // remove our handlers
            _client.OpenOrder -= clientOnOpenOrder;
            _client.OpenOrderEnd -= clientOnOpenOrderEnd;

            if (exception != null)
            {
                throw new Exception("InteractiveBrokersBrokerage.GetOpenOrders(): ", exception);
            }

            if (timedOut)
            {
                throw new TimeoutException("InteractiveBrokersBrokerage.GetOpenOrders(): Operation took longer than 15 seconds.");
            }

            if (all)
            {
                // https://interactivebrokers.github.io/tws-api/order_submission.html
                // if the function reqAllOpenOrders is used by a client, subsequent orders placed by that client
                // must have order IDs greater than the order IDs of all orders returned because of that function call.

                if (lastOrderId >= _nextValidId)
                {
                    Log.Trace($"InteractiveBrokersBrokerage.GetOpenOrders(): Updating nextValidId from {_nextValidId} to {lastOrderId + 1}");
                    _nextValidId = lastOrderId + 1;
                }
            }

            // convert results to Lean Orders outside the eventhandler to avoid nesting requests, as conversion may request
            // contract details
            return orders.Select(orderContract => ConvertOrder(orderContract.Order, orderContract.Contract)).ToList();
        }

        /// <summary>
        /// Gets all holdings for the account
        /// </summary>
        /// <returns>The current holdings from the account</returns>
        public override List<Holding> GetAccountHoldings()
        {
            if (!IsConnected)
            {
                Log.Trace("InteractiveBrokersBrokerage.GetAccountHoldings(): not connected, connecting now");
                Connect();
            }

            var utcNow = DateTime.UtcNow;
            var holdings = new List<Holding>();

            foreach (var kvp in _accountData.AccountHoldings)
            {
                var holding = ObjectActivator.Clone(kvp.Value);

                if (holding.Quantity != 0)
                {
                    if (OptionSymbol.IsOptionContractExpired(holding.Symbol, utcNow))
                    {
                        OnMessage(
                            new BrokerageMessageEvent(
                                BrokerageMessageType.Warning,
                                "ExpiredOptionHolding",
                                $"The option holding for [{holding.Symbol.Value}] is expired and will be excluded from the account holdings."));

                        continue;
                    }

                    holdings.Add(holding);
                }
            }

            return holdings;
        }

        /// <summary>
        /// Gets the current cash balance for each currency held in the brokerage account
        /// </summary>
        /// <returns>The current cash balance for each currency available for trading</returns>
        public override List<CashAmount> GetCashBalance()
        {
            if (!IsConnected)
            {
                if (_ibAutomater.IsWithinScheduledServerResetTimes())
                {
                    // Occasionally the disconnection due to the IB reset period might last
                    // much longer than expected during weekends (even up to the cash sync time).
                    // In this case we do not try to reconnect (since this would fail anyway)
                    // but we return the existing balances instead.
                    Log.Trace("InteractiveBrokersBrokerage.GetCashBalance(): not connected within reset times, returning existing balances");
                }
                else
                {
                    Log.Trace("InteractiveBrokersBrokerage.GetCashBalance(): not connected, connecting now");
                    Connect();
                }
            }

            var balances = _accountData.CashBalances.Select(x => new CashAmount(x.Value, x.Key)).ToList();

            if (balances.Count == 0)
            {
                Log.Trace($"InteractiveBrokersBrokerage.GetCashBalance(): no balances found, IsConnected: {IsConnected}, _disconnected1100Fired: {_stateManager.Disconnected1100Fired}");
            }

            return balances;
        }

        /// <summary>
        /// Gets the execution details matching the filter
        /// </summary>
        /// <returns>A list of executions matching the filter</returns>
        public List<IB.ExecutionDetailsEventArgs> GetExecutions(string symbol, string type, string exchange, DateTime? timeSince, string side)
        {
            var filter = new ExecutionFilter
            {
                AcctCode = _account,
                ClientId = ClientId,
                Exchange = exchange,
                SecType = type ?? IB.SecurityType.Undefined,
                Symbol = symbol,
                Time = (timeSince ?? DateTime.MinValue).ToStringInvariant("yyyyMMdd HH:mm:ss"),
                Side = side ?? IB.ActionSide.Undefined
            };

            var details = new List<IB.ExecutionDetailsEventArgs>();

            var manualResetEvent = new ManualResetEvent(false);

            var requestId = GetNextId();

            _requestInformation[requestId] = $"[Id={requestId}] GetExecutions: " + symbol;

            // define our event handlers
            EventHandler<IB.RequestEndEventArgs> clientOnExecutionDataEnd = (sender, args) =>
            {
                if (args.RequestId == requestId) manualResetEvent.Set();
            };
            EventHandler<IB.ExecutionDetailsEventArgs> clientOnExecDetails = (sender, args) =>
            {
                if (args.RequestId == requestId) details.Add(args);
            };

            _client.ExecutionDetails += clientOnExecDetails;
            _client.ExecutionDetailsEnd += clientOnExecutionDataEnd;

            CheckRateLimiting();

            // no need to be fancy with request id since that's all this client does is 1 request
            _client.ClientSocket.reqExecutions(requestId, filter);

            if (!manualResetEvent.WaitOne(5000))
            {
                throw new TimeoutException("InteractiveBrokersBrokerage.GetExecutions(): Operation took longer than 5 seconds.");
            }

            // remove our event handlers
            _client.ExecutionDetails -= clientOnExecDetails;
            _client.ExecutionDetailsEnd -= clientOnExecutionDataEnd;

            return details;
        }

        /// <summary>
        /// Connects the client to the IB gateway
        /// </summary>
        public override void Connect()
        {
            if (IsConnected) return;

            // we're going to receive fresh values for all account data, so we clear all
            _accountData.Clear();

            var attempt = 1;
            const int maxAttempts = 5;

            var subscribedSymbolsCount = _subscribedSymbols.Skip(0).Count();
            if (subscribedSymbolsCount > 0)
            {
                Log.Trace($"InteractiveBrokersBrokerage.Connect(): Data subscription count {subscribedSymbolsCount}, restoring data subscriptions is required");
            }

            while (true)
            {
                try
                {
                    Log.Trace("InteractiveBrokersBrokerage.Connect(): Attempting to connect ({0}/{1}) ...", attempt, maxAttempts);

                    // if message processing thread is still running, wait until it terminates
                    Disconnect();

                    // At initial startup or after a gateway restart, we need to wait for the gateway to be ready for a connect request.
                    // Attempting to connect to the socket too early will get a SocketException: Connection refused.
                    if (attempt == 1)
                    {
                        Thread.Sleep(2500);
                    }

                    _connectEvent.Reset();

                    // we're going to try and connect several times, if successful break
                    Log.Trace("InteractiveBrokersBrokerage.Connect(): calling _client.ClientSocket.eConnect()");
                    _client.ClientSocket.eConnect(_host, _port, ClientId);

                    if (!_connectEvent.WaitOne(TimeSpan.FromSeconds(15)))
                    {
                        Log.Error("InteractiveBrokersBrokerage.Connect(): timeout waiting for connect callback");
                    }

                    // create the message processing thread
                    var reader = new EReader(_client.ClientSocket, _signal);
                    reader.Start();

                    _messageProcessingThread = new Thread(() =>
                    {
                        Log.Trace("InteractiveBrokersBrokerage.Connect(): IB message processing thread started: #" + Thread.CurrentThread.ManagedThreadId);

                        while (_client.ClientSocket.IsConnected())
                        {
                            try
                            {
                                _signal.waitForSignal();
                                reader.processMsgs();
                            }
                            catch (Exception error)
                            {
                                // error in message processing thread, log error and disconnect
                                Log.Error("InteractiveBrokersBrokerage.Connect(): Error in message processing thread #" + Thread.CurrentThread.ManagedThreadId + ": " + error);
                            }
                        }

                        Log.Trace("InteractiveBrokersBrokerage.Connect(): IB message processing thread ended: #" + Thread.CurrentThread.ManagedThreadId);
                    })
                    { IsBackground = true };

                    _messageProcessingThread.Start();

                    // pause for a moment to receive next valid ID message from gateway
                    if (!_waitForNextValidId.WaitOne(15000))
                    {
                        // no response, disconnect and retry
                        Disconnect();

                        // max out at 5 attempts to connect ~1 minute
                        if (attempt++ < maxAttempts)
                        {
                            Thread.Sleep(1000);
                            continue;
                        }

                        throw new TimeoutException("InteractiveBrokersBrokerage.Connect(): Operation took longer than 15 seconds.");
                    }

                    Log.Trace("InteractiveBrokersBrokerage.Connect(): IB next valid id received.");

                    if (!_client.Connected) throw new Exception("InteractiveBrokersBrokerage.Connect(): Connection returned but was not in connected state.");

                    // request account information for logging purposes
                    _client.ClientSocket.reqAccountSummary(GetNextId(), "All", "AccountType");
                    _client.ClientSocket.reqManagedAccts();
                    _client.ClientSocket.reqFamilyCodes();

                    if (IsFinancialAdvisor)
                    {
                        if (!DownloadFinancialAdvisorAccount())
                        {
                            Log.Trace("InteractiveBrokersBrokerage.Connect(): DownloadFinancialAdvisorAccount failed.");

                            Disconnect();

                            if (_accountHoldingsLastException != null)
                            {
                                // if an exception was thrown during account download, do not retry but exit immediately
                                attempt = maxAttempts;
                                throw new Exception(_accountHoldingsLastException.Message, _accountHoldingsLastException);
                            }

                            if (attempt++ < maxAttempts)
                            {
                                Thread.Sleep(1000);
                                continue;
                            }

                            throw new TimeoutException("InteractiveBrokersBrokerage.Connect(): DownloadFinancialAdvisorAccount failed.");
                        }
                    }
                    else
                    {
                        if (!DownloadAccount())
                        {
                            Log.Trace("InteractiveBrokersBrokerage.Connect(): DownloadAccount failed. Operation took longer than 15 seconds.");

                            Disconnect();

                            if (_accountHoldingsLastException != null)
                            {
                                // if an exception was thrown during account download, do not retry but exit immediately
                                attempt = maxAttempts;
                                throw new Exception(_accountHoldingsLastException.Message, _accountHoldingsLastException);
                            }

                            if (attempt++ < maxAttempts)
                            {
                                Thread.Sleep(1000);
                                continue;
                            }

                            throw new TimeoutException("InteractiveBrokersBrokerage.Connect(): DownloadAccount failed.");
                        }
                    }

                    // enable logging at Warning level
                    _client.ClientSocket.setServerLogLevel(3);

                    break;
                }
                catch (Exception err)
                {
                    // max out at 5 attempts to connect ~1 minute
                    if (attempt++ < maxAttempts)
                    {
                        Thread.Sleep(15000);
                        continue;
                    }

                    // we couldn't connect after several attempts, log the error and throw an exception
                    Log.Error(err);

                    throw;
                }
            }

            // if we reached here we should be connected, check just in case
            if (IsConnected)
            {
                Log.Trace("InteractiveBrokersBrokerage.Connect(): Restoring data subscriptions...");
                RestoreDataSubscriptions();

                // we need to tell the DefaultBrokerageMessageHandler we are connected else he will kill us
                OnMessage(BrokerageMessageEvent.Reconnected("Connect() finished successfully"));
            }
            else
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "ConnectionState", "Unexpected, not connected state. Unable to connect to Interactive Brokers. Terminating algorithm."));
            }
        }

        private bool HeartBeat(int waitTimeMs)
        {
            if (_cancellationTokenSource.Token.WaitHandle.WaitOne(Time.GetSecondUnevenWait(waitTimeMs)))
            {
                // cancel signal
                return true;
            }

            if (!_ibAutomater.IsWithinScheduledServerResetTimes() && IsConnected)
            {
                _currentTimeEvent.Reset();
                // request current time to the server
                _client.ClientSocket.reqCurrentTime();
                var result = _currentTimeEvent.WaitOne(Time.GetSecondUnevenWait(waitTimeMs), _cancellationTokenSource.Token);
                if (!result)
                {
                    Log.Error("InteractiveBrokersBrokerage.HeartBeat(): failed!", overrideMessageFloodProtection: true);
                }
                return result;
            }
            // expected
            return true;
        }

        private void RunHeartBeatThread()
        {
            _heartBeatThread = new Thread(() =>
            {
                Log.Trace("InteractiveBrokersBrokerage.RunHeartBeatThread(): starting...");
                var waitTimeMs = 1000 * 60 * 2;
                try
                {
                    while (!_cancellationTokenSource.IsCancellationRequested)
                    {
                        if (!HeartBeat(waitTimeMs))
                        {
                            // just in case we were unlucky, we are reconnecting or similar let's retry with a longer wait
                            if (!HeartBeat(waitTimeMs * 3))
                            {
                                // we emit the disconnected event so that if the re connection bellow fails it will kill the algorithm
                                OnMessage(BrokerageMessageEvent.Disconnected("Connection with Interactive Brokers lost. Heart beat failed."));
                                try
                                {
                                    Disconnect();
                                }
                                catch (Exception)
                                {
                                }
                                Connect();
                            }
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    // expected
                }
                catch (OperationCanceledException)
                {
                    // expected
                }
                catch (Exception e)
                {
                    Log.Error(e, "HeartBeat");
                }

                Log.Trace("InteractiveBrokersBrokerage.RunHeartBeatThread(): ended");
            })
            { IsBackground = true, Name = "IbHeartBeat" };

            _heartBeatThread.Start();
        }

        /// <summary>
        /// Downloads the financial advisor configuration information.
        /// This method is called upon successful connection.
        /// </summary>
        private bool DownloadFinancialAdvisorAccount()
        {
            if (!_accountData.FinancialAdvisorConfiguration.Load(_client))
                return false;

            // Only one account can be subscribed at a time.
            // With Financial Advisory (FA) account structures there is an alternative way of
            // specifying the account code such that information is returned for 'All' sub accounts.
            // This is done by appending the letter 'A' to the end of the account number
            // https://interactivebrokers.github.io/tws-api/account_updates.html#gsc.tab=0

            // subscribe to the FA account
            return DownloadAccount();
        }

        private string GetAccountName()
        {
            return IsFinancialAdvisor ? $"{_account}A" : _account;
        }

        /// <summary>
        /// Downloads the account information and subscribes to account updates.
        /// This method is called upon successful connection.
        /// </summary>
        private bool DownloadAccount()
        {
            var account = GetAccountName();
            Log.Trace($"InteractiveBrokersBrokerage.DownloadAccount(): Downloading account data for {account}");

            _accountHoldingsLastException = null;
            _accountHoldingsResetEvent.Reset();

            // define our event handler, this acts as stop to make sure when we leave Connect we have downloaded the full account
            EventHandler<IB.AccountDownloadEndEventArgs> clientOnAccountDownloadEnd = (sender, args) =>
            {
                Log.Trace("InteractiveBrokersBrokerage.DownloadAccount(): Finished account download for " + args.Account);
                _accountHoldingsResetEvent.Set();
            };
            _client.AccountDownloadEnd += clientOnAccountDownloadEnd;

            // we'll wait to get our first account update, we need to be absolutely sure we
            // have downloaded the entire account before leaving this function
            using var firstAccountUpdateReceived = new ManualResetEvent(false);
            using var accountIsNotReady = new ManualResetEvent(false);
            EventHandler<IB.UpdateAccountValueEventArgs> clientOnUpdateAccountValue = (sender, args) =>
            {
                if(args != null && !string.IsNullOrEmpty(args.Key)
                    && string.Equals(args.Key, "accountReady", StringComparison.InvariantCultureIgnoreCase)
                    && bool.TryParse(args.Value, out var isReady))
                {
                    if (!isReady)
                    {
                        accountIsNotReady.Set();
                    }
                }
                firstAccountUpdateReceived.Set();
            };

            _client.UpdateAccountValue += clientOnUpdateAccountValue;

            // first we won't subscribe, wait for this to finish, below we'll subscribe for continuous updates
            _client.ClientSocket.reqAccountUpdates(true, account);

            // wait to see the first account value update
            firstAccountUpdateReceived.WaitOne(2500);

            // take pause to ensure the account is downloaded before continuing, this was added because running in
            // linux there appears to be different behavior where the account download end fires immediately.
            Thread.Sleep(2500);

            var result = _accountHoldingsResetEvent.WaitOne(15000);

            // remove our event handlers
            _client.AccountDownloadEnd -= clientOnAccountDownloadEnd;
            _client.UpdateAccountValue -= clientOnUpdateAccountValue;

            if(!result)
            {
                Log.Trace("InteractiveBrokersBrokerage.DownloadAccount(): Operation took longer than 15 seconds.");
            }

            if (accountIsNotReady.WaitOne(TimeSpan.Zero))
            {
                result = false;
                Log.Error("InteractiveBrokersBrokerage.DownloadAccount(): Account is not ready! Means that the IB server is in the process of resetting. Wait 30min and retry...");
                _cancellationTokenSource.Token.WaitHandle.WaitOne(TimeSpan.FromMinutes(30));
            }

            return result && _accountHoldingsLastException == null;
        }

        /// <summary>
        /// Disconnects the client from the IB gateway
        /// </summary>
        public override void Disconnect()
        {
            try
            {
                if (_client != null && _client.ClientSocket != null && _client.Connected)
                {
                    // unsubscribe from account updates
                    _client.ClientSocket.reqAccountUpdates(subscribe: false, GetAccountName());
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception);
            }
            _client?.ClientSocket.eDisconnect();

            if (_messageProcessingThread != null)
            {
                _signal.issueSignal();
                _messageProcessingThread.Join();
                _messageProcessingThread = null;
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public override void Dispose()
        {
            if (_isDisposeCalled)
            {
                return;
            }

            Log.Trace("InteractiveBrokersBrokerage.Dispose(): Disposing of IB resources.");

            _isDisposeCalled = true;

            _heartBeatThread.StopSafely(TimeSpan.FromSeconds(10), _cancellationTokenSource);

            if (_client != null)
            {
                Disconnect();
                _client.Dispose();
            }

            _aggregator.DisposeSafely();
            _ibAutomater?.Stop();

            _messagingRateLimiter.Dispose();
        }

        /// <summary>
        /// Initialize the instance of this class
        /// </summary>
        /// <param name="algorithm">The algorithm instance</param>
        /// <param name="orderProvider">An instance of IOrderProvider used to fetch Order objects by brokerage ID</param>
        /// <param name="securityProvider">The security provider used to give access to algorithm securities</param>
        /// <param name="aggregator">consolidate ticks</param>
        /// <param name="mapFileProvider">representing all the map files</param>
        /// <param name="account">The Interactive Brokers account name</param>
        /// <param name="host">host name or IP address of the machine where TWS is running. Leave blank to connect to the local host.</param>
        /// <param name="port">must match the port specified in TWS on the Configure&gt;API&gt;Socket Port field.</param>
        /// <param name="ibDirectory">The IB Gateway root directory</param>
        /// <param name="ibVersion">The IB Gateway version</param>
        /// <param name="userName">The login user name</param>
        /// <param name="password">The login password</param>
        /// <param name="tradingMode">The trading mode: 'live' or 'paper'</param>
        /// <param name="agentDescription">Used for Rule 80A describes the type of trader.</param>
        /// <param name="loadExistingHoldings">False will ignore existing security holdings from being loaded.</param>
        private void Initialize(
            IAlgorithm algorithm,
            IOrderProvider orderProvider,
            ISecurityProvider securityProvider,
            IDataAggregator aggregator,
            IMapFileProvider mapFileProvider,
            string account,
            string host,
            int port,
            string ibDirectory,
            string ibVersion,
            string userName,
            string password,
            string tradingMode,
            string agentDescription = IB.AgentDescription.Individual,
            bool loadExistingHoldings = true)
        {
            if (_isInitialized)
            {
                return;
            }
            _isInitialized = true;
            _loadExistingHoldings = loadExistingHoldings;
            _algorithm = algorithm;
            _orderProvider = orderProvider;
            _securityProvider = securityProvider;
            _aggregator = aggregator;
            _account = account;
            _host = host;
            _port = port;
            _ibVersion = Convert.ToInt32(ibVersion, CultureInfo.InvariantCulture);
            _agentDescription = agentDescription;

            _symbolMapper = new InteractiveBrokersSymbolMapper(mapFileProvider);

            _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
            _subscriptionManager.SubscribeImpl += (s, t) => Subscribe(s);
            _subscriptionManager.UnsubscribeImpl += (s, t) => Unsubscribe(s);

            Log.Trace("InteractiveBrokersBrokerage.InteractiveBrokersBrokerage(): Starting IB Automater...");

            // start IB Gateway
            var exportIbGatewayLogs = true; // Config.GetBool("ib-export-ibgateway-logs");
            _ibAutomater = new IBAutomater.IBAutomater(ibDirectory, ibVersion, userName, password, tradingMode, port, exportIbGatewayLogs);
            _ibAutomater.OutputDataReceived += OnIbAutomaterOutputDataReceived;
            _ibAutomater.ErrorDataReceived += OnIbAutomaterErrorDataReceived;
            _ibAutomater.Exited += OnIbAutomaterExited;
            _ibAutomater.Restarted += OnIbAutomaterRestarted;

            CheckIbAutomaterError(_ibAutomater.Start(false));

            Log.Trace($"InteractiveBrokersBrokerage.InteractiveBrokersBrokerage(): Host: {host}, Port: {port}, Account: {account}, AgentDescription: {agentDescription}");

            _client = new IB.InteractiveBrokersClient(_signal);

            // set up event handlers
            _client.UpdatePortfolio += HandlePortfolioUpdates;
            _client.OrderStatus += HandleOrderStatusUpdates;
            _client.OpenOrder += HandleOpenOrder;
            _client.OpenOrderEnd += HandleOpenOrderEnd;
            _client.UpdateAccountValue += HandleUpdateAccountValue;
            _client.AccountSummary += HandleAccountSummary;
            _client.ManagedAccounts += HandleManagedAccounts;
            _client.FamilyCodes += HandleFamilyCodes;
            _client.ExecutionDetails += HandleExecutionDetails;
            _client.CommissionReport += HandleCommissionReport;
            _client.Error += HandleError;
            _client.TickPrice += HandleTickPrice;
            _client.TickSize += HandleTickSize;
            _client.CurrentTimeUtc += HandleBrokerTime;

            // we need to wait until we receive the next valid id from the server
            _client.NextValidId += (sender, e) =>
            {
                lock (_nextValidIdLocker)
                {
                    Log.Trace($"InteractiveBrokersBrokerage.HandleNextValidID(): updating nextValidId from {_nextValidId} to {e.OrderId}");

                    _nextValidId = e.OrderId;
                    _waitForNextValidId.Set();
                }
            };

            _client.ConnectAck += (sender, e) =>
            {
                Log.Trace($"InteractiveBrokersBrokerage.HandleConnectAck(): API client connected [Server Version: {_client.ClientSocket.ServerVersion}].");
                _connectEvent.Set();
            };

            _client.ConnectionClosed += (sender, e) =>
            {
                Log.Trace($"InteractiveBrokersBrokerage.HandleConnectionClosed(): API client disconnected [Server Version: {_client.ClientSocket.ServerVersion}].");
                _connectEvent.Set();
            };

            ValidateSubscription();

            // initialize our heart beat thread
            RunHeartBeatThread();
        }

        /// <summary>
        /// Places the order with InteractiveBrokers
        /// </summary>
        /// <param name="order">The order to be placed</param>
        /// <param name="needsNewId">Set to true to generate a new order ID, false to leave it alone</param>
        /// <param name="exchange">The exchange to send the order to, defaults to "Smart" to use IB's smart routing</param>
        private void IBPlaceOrder(Order order, bool needsNewId, string exchange = null)
        {
            // MOO/MOC require directed option orders.
            // We resolve non-equity markets in the `CreateContract` method.
            if (exchange == null &&
                order.Symbol.SecurityType == SecurityType.Option &&
                (order.Type == OrderType.MarketOnOpen || order.Type == OrderType.MarketOnClose))
            {
                exchange = Market.CBOE.ToUpperInvariant();
            }

            var contract = CreateContract(order.Symbol, false, exchange);

            int ibOrderId;
            if (needsNewId)
            {
                // the order ids are generated for us by the SecurityTransactionManaer
                var id = GetNextId();
                order.BrokerId.Add(id.ToStringInvariant());
                ibOrderId = id;
            }
            else if (order.BrokerId.Any())
            {
                // this is *not* perfect code
                ibOrderId = Parse.Int(order.BrokerId[0]);
            }
            else
            {
                throw new ArgumentException("Expected order with populated BrokerId for updating orders.");
            }

            _requestInformation[ibOrderId] = $"[Id={ibOrderId}] IBPlaceOrder: {order.Symbol.Value} ({GetContractDescription(contract)} )";

            CheckRateLimiting();

            if (order.Type == OrderType.OptionExercise)
            {
                // IB API requires exerciseQuantity to be positive
                _client.ClientSocket.exerciseOptions(ibOrderId, contract, 1, decimal.ToInt32(order.AbsoluteQuantity), _account, 0);
            }
            else
            {
                var outsideRth = false;

                if (order.Type == OrderType.Limit ||
                    order.Type == OrderType.LimitIfTouched ||
                    order.Type == OrderType.StopMarket ||
                    order.Type == OrderType.StopLimit)
                {
                    var orderProperties = order.Properties as InteractiveBrokersOrderProperties;
                    if (orderProperties != null)
                    {
                        outsideRth = orderProperties.OutsideRegularTradingHours;
                    }
                }

                ManualResetEventSlim eventSlim = null;
                if (order.Type != OrderType.MarketOnOpen)
                {
                    _pendingOrderResponse[ibOrderId] = eventSlim = new ManualResetEventSlim(false);
                }

                var ibOrder = ConvertOrder(order, contract, ibOrderId, outsideRth);
                _client.ClientSocket.placeOrder(ibOrder.OrderId, contract, ibOrder);

                if(order.Type != OrderType.MarketOnOpen)
                {
                    if (!eventSlim.Wait(_responseTimeout))
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "NoBrokerageResponse", $"Timeout waiting for brokerage response for brokerage order id {ibOrderId} lean id {order.Id}"));
                    }
                    else
                    {
                        eventSlim.DisposeSafely();
                    }
                }
            }
        }

        private static string GetUniqueKey(Contract contract)
        {
            return $"{contract.ToString().ToUpperInvariant()} {contract.LastTradeDateOrContractMonth.ToStringInvariant()} {contract.Strike.ToStringInvariant()} {contract.Right}";
        }

        /// <summary>
        /// Get Contract Description
        /// </summary>
        /// <param name="contract">Contract to retrieve description of</param>
        /// <returns>string description</returns>
        public static string GetContractDescription(Contract contract)
        {
            return $"{contract} {contract.PrimaryExch ?? string.Empty} {contract.LastTradeDateOrContractMonth.ToStringInvariant()} {contract.Strike.ToStringInvariant()} {contract.Right}";
        }

        private string GetPrimaryExchange(Contract contract, Symbol symbol)
        {
            ContractDetails details;
            if (_contractDetails.TryGetValue(GetUniqueKey(contract), out details))
            {
                return details.Contract.PrimaryExch;
            }

            details = GetContractDetails(contract, symbol.Value);
            if (details == null)
            {
                // we were unable to find the contract details
                return null;
            }

            return details.Contract.PrimaryExch;
        }

        private string GetTradingClass(Contract contract, Symbol symbol)
        {
            ContractDetails details;
            if (_contractDetails.TryGetValue(GetUniqueKey(contract), out details))
            {
                return details.Contract.TradingClass;
            }

            if (symbol.SecurityType == SecurityType.FutureOption || symbol.SecurityType == SecurityType.IndexOption)
            {
                // Futures options and Index Options trading class is the same as the FOP ticker.
                // This is required in order to resolve the contract details successfully.
                // We let this method complete even though we assign twice so that the
                // contract details are added to the cache and won't require another lookup.
                contract.TradingClass = symbol.ID.Symbol;
            }

            details = GetContractDetails(contract, symbol.Value);
            if (details == null)
            {
                // we were unable to find the contract details
                return null;
            }

            return details.Contract.TradingClass;
        }

        private decimal GetMinTick(Contract contract, string ticker)
        {
            ContractDetails details;
            if (_contractDetails.TryGetValue(GetUniqueKey(contract), out details))
            {
                return (decimal)details.MinTick;
            }

            details = GetContractDetails(contract, ticker);
            if (details == null)
            {
                // we were unable to find the contract details
                return 0;
            }

            return (decimal)details.MinTick;
        }

        /// <summary>
        /// Will return and cache the IB contract details for the requested contract
        /// </summary>
        /// <param name="contract">The target contract</param>
        /// <param name="ticker">The associated Lean ticker. Just used for logging, can be provided empty</param>
        private ContractDetails GetContractDetails(Contract contract, string ticker)
        {
            const int timeout = 60; // sec

            var requestId = GetNextId();

            var contractDetailsList = new List<ContractDetails>();

            Log.Trace($"InteractiveBrokersBrokerage.GetContractDetails(): {ticker} ({contract})");

            _requestInformation[requestId] = $"[Id={requestId}] GetContractDetails: {ticker} ({contract})";

            var manualResetEvent = new ManualResetEvent(false);

            // define our event handlers
            EventHandler<IB.ContractDetailsEventArgs> clientOnContractDetails = (sender, args) =>
            {
                // ignore other requests
                if (args.RequestId != requestId)
                {
                    return;
                }

                var details = args.ContractDetails;
                contractDetailsList.Add(details);

                var uniqueKey = GetUniqueKey(details.Contract);
                _contractDetails.TryAdd(uniqueKey, details);

                Log.Trace($"InteractiveBrokersBrokerage.GetContractDetails(): clientOnContractDetails event: {uniqueKey}");
            };

            EventHandler<IB.RequestEndEventArgs> clientOnContractDetailsEnd = (sender, args) =>
            {
                if (args.RequestId == requestId)
                {
                    manualResetEvent.Set();
                }
            };

            EventHandler<IB.ErrorEventArgs> clientOnError = (sender, args) =>
            {
                if (args.Id == requestId)
                {
                    manualResetEvent.Set();
                }
            };

            _client.ContractDetails += clientOnContractDetails;
            _client.ContractDetailsEnd += clientOnContractDetailsEnd;
            _client.Error += clientOnError;

            CheckRateLimiting();

            // make the request for data
            _client.ClientSocket.reqContractDetails(requestId, contract);

            if (!manualResetEvent.WaitOne(timeout * 1000))
            {
                Log.Error("InteractiveBrokersBrokerage.GetContractDetails(): failed to receive response from IB within {0} seconds", timeout);
            }

            // be sure to remove our event handlers
            _client.Error -= clientOnError;
            _client.ContractDetailsEnd -= clientOnContractDetailsEnd;
            _client.ContractDetails -= clientOnContractDetails;

            Log.Trace($"InteractiveBrokersBrokerage.GetContractDetails(): contracts found: {contractDetailsList.Count}");

            return contractDetailsList.FirstOrDefault();
        }

        /// <summary>
        /// Helper method to normalize a provided price to the Lean expected unit
        /// </summary>
        /// <param name="price">Price to be normalized</param>
        /// <param name="symbol">Symbol from which we need to get the PriceMagnifier attribute to normalize the price</param>
        /// <returns>The price normalized to LEAN expected unit</returns>
        private decimal NormalizePriceToLean(double price, Symbol symbol)
        {
            var symbolProperties = _symbolPropertiesDatabase.GetSymbolProperties(symbol.ID.Market, symbol, symbol.SecurityType, Currencies.USD);
            return Convert.ToDecimal(price) / symbolProperties.PriceMagnifier;
        }

        /// <summary>
        /// Helper method to normalize a provided price to the brokerage expected unit, for example cents,
        /// applying rounding to minimum tick size
        /// </summary>
        /// <param name="price">Price to be normalized</param>
        /// <param name="contract">Contract of the symbol</param>
        /// <param name="symbol">The symbol from which we need to get the PriceMagnifier attribute to normalize the price</param>
        /// <param name="minTick">The minimum allowed price variation</param>
        /// <returns>The price normalized to be brokerage expected unit</returns>
        private double NormalizePriceToBrokerage(decimal price, Contract contract, Symbol symbol, decimal? minTick = null)
        {
            var symbolProperties = _symbolPropertiesDatabase.GetSymbolProperties(symbol.ID.Market, symbol, symbol.SecurityType, Currencies.USD);
            var roundedPrice = RoundPrice(price, minTick ?? GetMinTick(contract, symbol.Value));
            roundedPrice *= symbolProperties.PriceMagnifier;
            return Convert.ToDouble(roundedPrice);
        }

        /// <summary>
        /// Find contract details given a ticker and contract
        /// </summary>
        /// <param name="contract">Contract we are searching for</param>
        /// <param name="ticker">Ticker of this contract</param>
        /// <returns></returns>
        public IEnumerable<ContractDetails> FindContracts(Contract contract, string ticker)
        {
            const int timeout = 60; // sec

            var requestId = GetNextId();

            _requestInformation[requestId] = $"[Id={requestId}] FindContracts: {ticker} ({GetContractDescription(contract)})";

            var manualResetEvent = new ManualResetEvent(false);
            var contractDetails = new List<ContractDetails>();

            // define our event handlers
            EventHandler<IB.ContractDetailsEventArgs> clientOnContractDetails = (sender, args) =>
            {
                if (args.RequestId == requestId)
                {
                    contractDetails.Add(args.ContractDetails);
                }
            };

            EventHandler<IB.RequestEndEventArgs> clientOnContractDetailsEnd = (sender, args) =>
            {
                if (args.RequestId == requestId)
                {
                    manualResetEvent.Set();
                }
            };

            EventHandler<IB.ErrorEventArgs> clientOnError = (sender, args) =>
            {
                if (args.Id == requestId)
                {
                    manualResetEvent.Set();
                }
            };

            _client.ContractDetails += clientOnContractDetails;
            _client.ContractDetailsEnd += clientOnContractDetailsEnd;
            _client.Error += clientOnError;

            CheckRateLimiting();

            // make the request for data
            _client.ClientSocket.reqContractDetails(requestId, contract);

            if (!manualResetEvent.WaitOne(timeout * 1000))
            {
                Log.Error("InteractiveBrokersBrokerage.FindContracts(): failed to receive response from IB within {0} seconds", timeout);
            }

            // be sure to remove our event handlers
            _client.Error -= clientOnError;
            _client.ContractDetailsEnd -= clientOnContractDetailsEnd;
            _client.ContractDetails -= clientOnContractDetails;

            return contractDetails;
        }

        /// <summary>
        /// Handles error messages from IB
        /// </summary>
        private void HandleError(object sender, IB.ErrorEventArgs e)
        {
            // handles the 'connection refused' connect cases
            _connectEvent.Set();

            // https://www.interactivebrokers.com/en/software/api/apiguide/tables/api_message_codes.htm

            var requestId = e.Id;
            var errorCode = e.Code;
            var errorMsg = e.Message;

            // rewrite these messages to be single lined
            errorMsg = errorMsg.Replace("\r\n", ". ").Replace("\r", ". ").Replace("\n", ". ");

            // if there is additional information for the originating request, append it to the error message
            string requestMessage;
            if (_requestInformation.TryGetValue(requestId, out requestMessage))
            {
                errorMsg += ". Origin: " + requestMessage;
            }

            // historical data request with no data returned
            if (errorCode == 162 && errorMsg.Contains("HMDS query returned no data"))
            {
                return;
            }

            Log.Trace($"InteractiveBrokersBrokerage.HandleError(): RequestId: {requestId} ErrorCode: {errorCode} - {errorMsg}");

            // figure out the message type based on our code collections below
            var brokerageMessageType = BrokerageMessageType.Information;
            if (ErrorCodes.Contains(errorCode))
            {
                brokerageMessageType = BrokerageMessageType.Error;
            }
            else if (WarningCodes.Contains(errorCode))
            {
                brokerageMessageType = BrokerageMessageType.Warning;
            }

            // code 1100 is a connection failure, we'll wait a minute before exploding gracefully
            if (errorCode == 1100)
            {
                if (!_stateManager.Disconnected1100Fired)
                {
                    _stateManager.Disconnected1100Fired = true;

                    // begin the try wait logic
                    TryWaitForReconnect();
                }
                else
                {
                    // The IB API sends many consecutive disconnect messages (1100) during nightly reset periods and weekends,
                    // so we send the message event only when we transition from connected to disconnected state,
                    // to avoid flooding the logs with the same message.
                    return;
                }
            }
            else if (errorCode == 1102)
            {
                // Connectivity between IB and TWS has been restored - data maintained.
                OnMessage(BrokerageMessageEvent.Reconnected(errorMsg));

                _stateManager.Disconnected1100Fired = false;
                return;
            }
            else if (errorCode == 1101)
            {
                // Connectivity between IB and TWS has been restored - data lost.
                OnMessage(BrokerageMessageEvent.Reconnected(errorMsg));

                _stateManager.Disconnected1100Fired = false;

                RestoreDataSubscriptions();
                return;
            }
            else if (errorCode == 506)
            {
                Log.Trace("InteractiveBrokersBrokerage.HandleError(): Server Version: " + _client.ClientSocket.ServerVersion);

                if (!_client.ClientSocket.IsConnected())
                {
                    // ignore the 506 error if we are not yet connected, will be checked by IB API later
                    // we have occasionally experienced this error after restarting IBGateway after the nightly reset
                    Log.Trace($"InteractiveBrokersBrokerage.HandleError(): Not connected, ignoring error, ErrorCode: {errorCode} - {errorMsg}");
                    return;
                }
            }

            if (InvalidatingCodes.Contains(errorCode))
            {
                // let's unblock the waiting thread right away
                if (_pendingOrderResponse.TryRemove(requestId, out var eventSlim))
                {
                    eventSlim.Set();
                }

                var message = $"{errorCode} - {errorMsg}";
                Log.Trace($"InteractiveBrokersBrokerage.HandleError.InvalidateOrder(): IBOrderId: {requestId} ErrorCode: {message}");

                // invalidate the order
                var order = _orderProvider.GetOrderByBrokerageId(requestId);
                if (order != null)
                {
                    var orderEvent = new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
                    {
                        Status = OrderStatus.Invalid,
                        Message = message
                    };
                    OnOrderEvent(orderEvent);
                }
                else
                {
                    Log.Error($"InteractiveBrokersBrokerage.HandleError.InvalidateOrder(): Unable to locate order with BrokerageID {requestId}");
                }
            }

            if (!FilteredCodes.Contains(errorCode))
            {
                OnMessage(new BrokerageMessageEvent(brokerageMessageType, errorCode, errorMsg));
            }
        }

        /// <summary>
        /// Restores data subscriptions existing before the IB Gateway restart
        /// </summary>
        private void RestoreDataSubscriptions()
        {
            List<Symbol> subscribedSymbols;
            lock (_sync)
            {
                subscribedSymbols = _subscribedSymbols.Keys.ToList();

                _subscribedSymbols.Clear();
                _subscribedTickers.Clear();
                _underlyings.Clear();
            }

            Subscribe(subscribedSymbols);
        }

        /// <summary>
        /// If we lose connection to TWS/IB servers we don't want to send the Error event if it is within
        /// the scheduled server reset times
        /// </summary>
        private void TryWaitForReconnect()
        {
            // IB has server reset schedule: https://www.interactivebrokers.com/en/?f=%2Fen%2Fsoftware%2FsystemStatus.php%3Fib_entity%3Dllc

            if (!_stateManager.Disconnected1100Fired)
            {
                return;
            }

            var isResetTime = _ibAutomater.IsWithinScheduledServerResetTimes();

            if (!isResetTime)
            {
                if (!_stateManager.PreviouslyInResetTime)
                {
                    // if we were disconnected and we're not within the reset times, send the error event
                    OnMessage(BrokerageMessageEvent.Disconnected("Connection with Interactive Brokers lost. " +
                                                                 "This could be because of internet connectivity issues or a log in from another location."
                        ));
                }
            }
            else
            {
                Log.Trace("InteractiveBrokersBrokerage.TryWaitForReconnect(): Within server reset times, trying to wait for reconnect...");

                // we're still not connected but we're also within the schedule reset time, so just keep polling
                Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => TryWaitForReconnect());
            }

            _stateManager.PreviouslyInResetTime = isResetTime;
        }

        /// <summary>
        /// Stores all the account values
        /// </summary>
        private void HandleUpdateAccountValue(object sender, IB.UpdateAccountValueEventArgs e)
        {
            //Log.Trace($"HandleUpdateAccountValue(): Key:{e.Key} Value:{e.Value} Currency:{e.Currency} AccountName:{e.AccountName}");

            try
            {
                _accountData.AccountProperties[e.Currency + ":" + e.Key] = e.Value;

                // we want to capture if the user's cash changes so we can reflect it in the algorithm
                if (e.Key == AccountValueKeys.CashBalance && e.Currency != "BASE")
                {
                    var cashBalance = decimal.Parse(e.Value, CultureInfo.InvariantCulture);
                    _accountData.CashBalances.AddOrUpdate(e.Currency, cashBalance);

                    OnAccountChanged(new AccountEvent(e.Currency, cashBalance));
                }

                // IB does not explicitly return the account base currency, but we can find out using exchange rates returned
                if (e.Key == AccountValueKeys.ExchangeRate && e.Currency != "BASE" && e.Value.ToDecimal() == 1)
                {
                    AccountBaseCurrency = e.Currency;
                }
            }
            catch (Exception err)
            {
                Log.Error("InteractiveBrokersBrokerage.HandleUpdateAccountValue(): " + err);
            }
        }

        /// <summary>
        /// Handle order events from IB
        /// </summary>
        private void HandleOrderStatusUpdates(object sender, IB.OrderStatusEventArgs update)
        {
            try
            {
                // let's unblock the waiting thread right away
                if (_pendingOrderResponse.TryRemove(update.OrderId, out var eventSlim))
                {
                    eventSlim.Set();
                }

                Log.Trace($"InteractiveBrokersBrokerage.HandleOrderStatusUpdates(): {update}");

                if (!IsConnected)
                {
                    if (_client != null)
                    {
                        Log.Error($"InteractiveBrokersBrokerage.HandleOrderStatusUpdates(): Not connected; update dropped, _client.Connected: {_client.Connected}, _disconnected1100Fired: {_stateManager.Disconnected1100Fired}");
                    }
                    else
                    {
                        Log.Error("InteractiveBrokersBrokerage.HandleOrderStatusUpdates(): Not connected; _client is null");
                    }
                    return;
                }

                var order = _orderProvider.GetOrderByBrokerageId(update.OrderId);
                if (order == null)
                {
                    Log.Error("InteractiveBrokersBrokerage.HandleOrderStatusUpdates(): Unable to locate order with BrokerageID " + update.OrderId);
                    return;
                }

                var status = ConvertOrderStatus(update.Status);

                if (status == OrderStatus.Filled || status == OrderStatus.PartiallyFilled)
                {
                    // fill events will be only processed in HandleExecutionDetails and HandleCommissionReports
                    return;
                }

                int id;
                // if we get a Submitted status and we had placed an order update, this new event is flagged as an update
                var isUpdate = status == OrderStatus.Submitted && _orderUpdates.TryRemove(order.Id, out id);

                // IB likes to duplicate/triplicate some events, so we fire non-fill events only if status changed
                if (status != order.Status || isUpdate)
                {
                    if (order.Status.IsClosed())
                    {
                        // if the order is already in a closed state, we ignore the event
                        Log.Trace("InteractiveBrokersBrokerage.HandleOrderStatusUpdates(): ignoring update in closed state - order.Status: " + order.Status + ", status: " + status);
                    }
                    else if (order.Status == OrderStatus.PartiallyFilled && (status == OrderStatus.New || status == OrderStatus.Submitted) && !isUpdate)
                    {
                        // if we receive a New or Submitted event when already partially filled, we ignore it
                        Log.Trace("InteractiveBrokersBrokerage.HandleOrderStatusUpdates(): ignoring status " + status + " after partial fills");
                    }
                    else
                    {
                        // fire the event
                        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Interactive Brokers Order Event")
                        {
                            Status = isUpdate ? OrderStatus.UpdateSubmitted : status
                        });
                    }
                }
            }
            catch (Exception err)
            {
                Log.Error("InteractiveBrokersBrokerage.HandleOrderStatusUpdates(): " + err);
            }
        }

        /// <summary>
        /// Handle OpenOrder event from IB
        /// </summary>
        private static void HandleOpenOrder(object sender, IB.OpenOrderEventArgs e)
        {
            Log.Trace($"InteractiveBrokersBrokerage.HandleOpenOrder(): {e}");
        }

        /// <summary>
        /// Handle OpenOrderEnd event from IB
        /// </summary>
        private static void HandleOpenOrderEnd(object sender, EventArgs e)
        {
            Log.Trace("InteractiveBrokersBrokerage.HandleOpenOrderEnd()");
        }

        /// <summary>
        /// Handle execution events from IB
        /// </summary>
        /// <remarks>
        /// This needs to be handled because if a market order is executed immediately, there will be no OrderStatus event
        /// https://interactivebrokers.github.io/tws-api/order_submission.html#order_status
        /// </remarks>
        private void HandleExecutionDetails(object sender, IB.ExecutionDetailsEventArgs executionDetails)
        {
            try
            {
                // There are not guaranteed to be orderStatus callbacks for every change in order status. For example with market orders when the order is accepted and executes immediately,
                // there commonly will not be any corresponding orderStatus callbacks. For that reason it is recommended to monitor the IBApi.EWrapper.execDetails function in addition to
                // IBApi.EWrapper.orderStatus. From IB API docs
                // let's unblock the waiting thread right away
                if (_pendingOrderResponse.TryRemove(executionDetails.Execution.OrderId, out var eventSlim))
                {
                    eventSlim.Set();
                }

                Log.Trace("InteractiveBrokersBrokerage.HandleExecutionDetails(): " + executionDetails);

                if (!IsConnected)
                {
                    if (_client != null)
                    {
                        Log.Error($"InteractiveBrokersBrokerage.HandleExecutionDetails(): Not connected; update dropped, _client.Connected: {_client.Connected}, _disconnected1100Fired: {_stateManager.Disconnected1100Fired}");
                    }
                    else
                    {
                        Log.Error("InteractiveBrokersBrokerage.HandleExecutionDetails(): Not connected; _client is null");
                    }
                    return;
                }

                var order = _orderProvider.GetOrderByBrokerageId(executionDetails.Execution.OrderId);
                if (order == null)
                {
                    Log.Error("InteractiveBrokersBrokerage.HandleExecutionDetails(): Unable to locate order with BrokerageID " + executionDetails.Execution.OrderId);
                    return;
                }

                // For financial advisor orders, we first receive executions and commission reports for the master order,
                // followed by executions and commission reports for all allocations.
                // We don't want to emit fills for these allocation events,
                // so we ignore events received after the order is completely filled or
                // executions for allocations which are already included in the master execution.

                CommissionReport commissionReport;
                if (_commissionReports.TryGetValue(executionDetails.Execution.ExecId, out commissionReport))
                {
                    if (CanEmitFill(order, executionDetails.Execution))
                    {
                        // we have both execution and commission report, emit the fill
                        EmitOrderFill(order, executionDetails, commissionReport);
                    }

                    _commissionReports.TryRemove(commissionReport.ExecId, out commissionReport);
                }
                else
                {
                    // save execution in dictionary and wait for commission report
                    _orderExecutions[executionDetails.Execution.ExecId] = executionDetails;
                }
            }
            catch (Exception err)
            {
                Log.Error("InteractiveBrokersBrokerage.HandleExecutionDetails(): " + err);
            }
        }

        /// <summary>
        /// Handle commission report events from IB
        /// </summary>
        /// <remarks>
        /// This method matches commission reports with previously saved executions and fires the OrderEvents.
        /// </remarks>
        private void HandleCommissionReport(object sender, IB.CommissionReportEventArgs e)
        {
            try
            {
                Log.Trace("InteractiveBrokersBrokerage.HandleCommissionReport(): " + e);

                IB.ExecutionDetailsEventArgs executionDetails;
                if (!_orderExecutions.TryGetValue(e.CommissionReport.ExecId, out executionDetails))
                {
                    // save commission in dictionary and wait for execution event
                    _commissionReports[e.CommissionReport.ExecId] = e.CommissionReport;
                    return;
                }

                var order = _orderProvider.GetOrderByBrokerageId(executionDetails.Execution.OrderId);
                if (order == null)
                {
                    Log.Error("InteractiveBrokersBrokerage.HandleExecutionDetails(): Unable to locate order with BrokerageID " + executionDetails.Execution.OrderId);
                    return;
                }

                if (CanEmitFill(order, executionDetails.Execution))
                {
                    // we have both execution and commission report, emit the fill
                    EmitOrderFill(order, executionDetails, e.CommissionReport);
                }

                // always remove previous execution
                _orderExecutions.TryRemove(e.CommissionReport.ExecId, out executionDetails);
            }
            catch (Exception err)
            {
                Log.Error("InteractiveBrokersBrokerage.HandleCommissionReport(): " + err);
            }
        }

        /// <summary>
        /// Decide which fills should be emitted, accounting for different types of Financial Advisor orders
        /// </summary>
        private bool CanEmitFill(Order order, Execution execution)
        {
            if (order.Status == OrderStatus.Filled)
                return false;

            // non-FA orders
            if (!IsFinancialAdvisor)
                return true;

            var orderProperties = order.Properties as InteractiveBrokersOrderProperties;
            if (orderProperties == null)
                return true;

            return
                // FA master orders for groups/profiles
                string.IsNullOrWhiteSpace(orderProperties.Account) && execution.AcctNumber == _account ||

                // FA orders for single managed accounts
                !string.IsNullOrWhiteSpace(orderProperties.Account) && execution.AcctNumber == orderProperties.Account;
        }

        /// <summary>
        /// Emits an order fill (or partial fill) including the actual IB commission paid
        /// </summary>
        private void EmitOrderFill(Order order, IB.ExecutionDetailsEventArgs executionDetails, CommissionReport commissionReport)
        {
            var currentQuantityFilled = Convert.ToInt32(executionDetails.Execution.Shares);
            var totalQuantityFilled = Convert.ToInt32(executionDetails.Execution.CumQty);
            var remainingQuantity = Convert.ToInt32(order.AbsoluteQuantity - totalQuantityFilled);
            var price = NormalizePriceToLean(executionDetails.Execution.Price, order.Symbol);
            var orderFee = new OrderFee(new CashAmount(
                Convert.ToDecimal(commissionReport.Commission),
                commissionReport.Currency.ToUpperInvariant()));

            // set order status based on remaining quantity
            var status = remainingQuantity > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Filled;

            // mark sells as negative quantities
            var fillQuantity = order.Direction == OrderDirection.Buy ? currentQuantityFilled : -currentQuantityFilled;
            var orderEvent = new OrderEvent(order, DateTime.UtcNow, orderFee, "Interactive Brokers Order Fill Event")
            {
                Status = status,
                FillPrice = price,
                FillQuantity = fillQuantity
            };
            if (remainingQuantity != 0)
            {
                orderEvent.Message += " - " + remainingQuantity + " remaining";
            }

            // fire the order fill event
            OnOrderEvent(orderEvent);
        }

        /// <summary>
        /// Handle portfolio changed events from IB
        /// </summary>
        private void HandlePortfolioUpdates(object sender, IB.UpdatePortfolioEventArgs e)
        {
            try
            {
                Log.Trace($"InteractiveBrokersBrokerage.HandlePortfolioUpdates(): {e}");

                // notify the transaction handler about all option position updates
                if (e.Contract.SecType is IB.SecurityType.Option or IB.SecurityType.FutureOption)
                {
                    var symbol = MapSymbol(e.Contract);

                    OnOptionNotification(new OptionNotificationEventArgs(symbol, e.Position));
                }

                _accountHoldingsResetEvent.Reset();
                if (_loadExistingHoldings)
                {
                    var holding = CreateHolding(e);
                    _accountData.AccountHoldings[holding.Symbol.Value] = holding;
                }
            }
            catch (Exception exception)
            {
                Log.Error($"InteractiveBrokersBrokerage.HandlePortfolioUpdates(): {exception}");

                if (e.Position != 0)
                {
                    // Force a runtime error only with a nonzero position for an unsupported security type,
                    // because after the user has manually closed the position and restarted the algorithm,
                    // he'll have a zero position but a nonzero realized PNL, so this event handler will be called again.

                    _accountHoldingsLastException = exception;
                    _accountHoldingsResetEvent.Set();
                }
            }
        }

        /// <summary>
        /// Converts a QC order to an IB order
        /// </summary>
        private IBApi.Order ConvertOrder(Order order, Contract contract, int ibOrderId, bool outsideRth)
        {
            var ibOrder = new IBApi.Order
            {
                ClientId = ClientId,
                OrderId = ibOrderId,
                Account = _account,
                Action = ConvertOrderDirection(order.Direction),
                TotalQuantity = (int)Math.Abs(order.Quantity),
                OrderType = ConvertOrderType(order.Type),
                AllOrNone = false,
                Tif = ConvertTimeInForce(order),
                Transmit = true,
                Rule80A = _agentDescription,
                OutsideRth = outsideRth
            };

            var gtdTimeInForce = order.TimeInForce as GoodTilDateTimeInForce;
            if (gtdTimeInForce != null)
            {
                DateTime expiryUtc;
                if (order.SecurityType == SecurityType.Forex)
                {
                    expiryUtc = gtdTimeInForce.GetForexOrderExpiryDateTime(order);
                }
                else
                {
                    var exchangeHours = MarketHoursDatabase.FromDataFolder()
                        .GetExchangeHours(order.Symbol.ID.Market, order.Symbol, order.SecurityType);

                    var expiry = exchangeHours.GetNextMarketClose(gtdTimeInForce.Expiry.Date, false);
                    expiryUtc = expiry.ConvertToUtc(exchangeHours.TimeZone);
                }

                // The IB format for the GoodTillDate order property is "yyyymmdd hh:mm:ss xxx" where yyyymmdd and xxx are optional.
                // E.g.: 20031126 15:59:00 EST
                // If no date is specified, current date is assumed. If no time-zone is specified, local time-zone is assumed.

                ibOrder.GoodTillDate = expiryUtc.ToString("yyyyMMdd HH:mm:ss UTC", CultureInfo.InvariantCulture);
            }

            var limitOrder = order as LimitOrder;
            var stopMarketOrder = order as StopMarketOrder;
            var stopLimitOrder = order as StopLimitOrder;
            var limitIfTouchedOrder = order as LimitIfTouchedOrder;
            if (limitOrder != null)
            {
                ibOrder.LmtPrice = NormalizePriceToBrokerage(limitOrder.LimitPrice, contract, order.Symbol);
            }
            else if (stopMarketOrder != null)
            {
                ibOrder.AuxPrice = NormalizePriceToBrokerage(stopMarketOrder.StopPrice, contract, order.Symbol);
            }
            else if (stopLimitOrder != null)
            {
                var minTick = GetMinTick(contract, order.Symbol.Value);
                ibOrder.LmtPrice = NormalizePriceToBrokerage(stopLimitOrder.LimitPrice, contract, order.Symbol, minTick);
                ibOrder.AuxPrice = NormalizePriceToBrokerage(stopLimitOrder.StopPrice, contract, order.Symbol, minTick);
            }
            else if (limitIfTouchedOrder != null)
            {
                var minTick = GetMinTick(contract, order.Symbol.Value);
                ibOrder.LmtPrice = NormalizePriceToBrokerage(limitIfTouchedOrder.LimitPrice, contract, order.Symbol, minTick);
                ibOrder.AuxPrice = NormalizePriceToBrokerage(limitIfTouchedOrder.TriggerPrice, contract, order.Symbol, minTick);
            }

            // add financial advisor properties
            if (IsFinancialAdvisor)
            {
                // https://interactivebrokers.github.io/tws-api/financial_advisor.html#gsc.tab=0

                var orderProperties = order.Properties as InteractiveBrokersOrderProperties;
                if (orderProperties != null)
                {
                    if (!string.IsNullOrWhiteSpace(orderProperties.Account))
                    {
                        // order for a single managed account
                        ibOrder.Account = orderProperties.Account;
                    }
                    else if (!string.IsNullOrWhiteSpace(orderProperties.FaProfile))
                    {
                        // order for an account profile
                        ibOrder.FaProfile = orderProperties.FaProfile;
                    }
                    else if (!string.IsNullOrWhiteSpace(orderProperties.FaGroup))
                    {
                        // order for an account group
                        ibOrder.FaGroup = orderProperties.FaGroup;
                        ibOrder.FaMethod = orderProperties.FaMethod;

                        if (ibOrder.FaMethod == "PctChange")
                        {
                            ibOrder.FaPercentage = orderProperties.FaPercentage.ToStringInvariant();
                            ibOrder.TotalQuantity = 0;
                        }
                    }
                }
            }

            // not yet supported
            //ibOrder.ParentId =
            //ibOrder.OcaGroup =

            return ibOrder;
        }

        private Order ConvertOrder(IBApi.Order ibOrder, Contract contract)
        {
            // this function is called by GetOpenOrders which is mainly used by the setup handler to
            // initialize algorithm state.  So the only time we'll be executing this code is when the account
            // has orders sitting and waiting from before algo initialization...
            // because of this we can't get the time accurately

            Order order;
            var mappedSymbol = MapSymbol(contract);
            var direction = ConvertOrderDirection(ibOrder.Action);
            var quantitySign = direction == OrderDirection.Sell ? -1 : 1;
            var orderType = ConvertOrderType(ibOrder);
            switch (orderType)
            {
                case OrderType.Market:
                    order = new MarketOrder(mappedSymbol,
                        Convert.ToInt32(ibOrder.TotalQuantity) * quantitySign,
                        new DateTime() // not sure how to get this data
                        );
                    break;

                case OrderType.MarketOnOpen:
                    order = new MarketOnOpenOrder(mappedSymbol,
                        Convert.ToInt32(ibOrder.TotalQuantity) * quantitySign,
                        new DateTime());
                    break;

                case OrderType.MarketOnClose:
                    order = new MarketOnCloseOrder(mappedSymbol,
                        Convert.ToInt32(ibOrder.TotalQuantity) * quantitySign,
                        new DateTime()
                        );
                    break;

                case OrderType.Limit:
                    order = new LimitOrder(mappedSymbol,
                        Convert.ToInt32(ibOrder.TotalQuantity) * quantitySign,
                        NormalizePriceToLean(ibOrder.LmtPrice, mappedSymbol),
                        new DateTime()
                        );
                    break;

                case OrderType.StopMarket:
                    order = new StopMarketOrder(mappedSymbol,
                        Convert.ToInt32(ibOrder.TotalQuantity) * quantitySign,
                        NormalizePriceToLean(ibOrder.AuxPrice, mappedSymbol),
                        new DateTime()
                        );
                    break;

                case OrderType.StopLimit:
                    order = new StopLimitOrder(mappedSymbol,
                        Convert.ToInt32(ibOrder.TotalQuantity) * quantitySign,
                        NormalizePriceToLean(ibOrder.AuxPrice, mappedSymbol),
                        NormalizePriceToLean(ibOrder.LmtPrice, mappedSymbol),
                        new DateTime()
                        );
                    break;

                case OrderType.LimitIfTouched:
                    order = new LimitIfTouchedOrder(mappedSymbol,
                        Convert.ToInt32(ibOrder.TotalQuantity) * quantitySign,
                        NormalizePriceToLean(ibOrder.AuxPrice, mappedSymbol),
                        NormalizePriceToLean(ibOrder.LmtPrice, mappedSymbol),
                        new DateTime()
                    );
                    break;

                default:
                    throw new InvalidEnumArgumentException("orderType", (int)orderType, typeof(OrderType));
            }

            order.BrokerId.Add(ibOrder.OrderId.ToStringInvariant());

            order.Properties.TimeInForce = ConvertTimeInForce(ibOrder.Tif, ibOrder.GoodTillDate);

            return order;
        }

        /// <summary>
        /// Creates an IB contract from the order.
        /// </summary>
        /// <param name="symbol">The symbol whose contract we need to create</param>
        /// <param name="includeExpired">Include expired contracts</param>
        /// <param name="exchange">The exchange where the order will be placed, defaults to 'Smart'</param>
        /// <returns>A new IB contract for the order</returns>
        private Contract CreateContract(Symbol symbol, bool includeExpired, string exchange = null)
        {
            var securityType = ConvertSecurityType(symbol.SecurityType);
            var ibSymbol = _symbolMapper.GetBrokerageSymbol(symbol);

            var symbolProperties = _symbolPropertiesDatabase.GetSymbolProperties(
                symbol.ID.Market,
                symbol,
                symbol.SecurityType,
                Currencies.USD);

            var contract = new Contract
            {
                Symbol = ibSymbol,
                Exchange = exchange ?? GetSymbolExchange(symbol),
                SecType = securityType,
                Currency = symbolProperties.QuoteCurrency
            };
            if (symbol.ID.SecurityType == SecurityType.Forex)
            {
                // forex is special, so rewrite some of the properties to make it work
                contract.Exchange = "IDEALPRO";
                contract.Symbol = ibSymbol.Substring(0, 3);
                contract.Currency = ibSymbol.Substring(3);
            }

            if (symbol.ID.SecurityType == SecurityType.Equity)
            {
                contract.PrimaryExch = GetPrimaryExchange(contract, symbol);
            }

            // Indexes requires that the exchange be specified exactly
            if (symbol.ID.SecurityType == SecurityType.Index)
            {
                contract.Exchange = IndexSymbol.GetIndexExchange(symbol);
            }

            if (symbol.ID.SecurityType.IsOption())
            {
                // Subtract a day from Index Options, since their last trading date
                // is on the day before the expiry.
                contract.LastTradeDateOrContractMonth = symbol.ID.Date
                    .AddDays(symbol.SecurityType == SecurityType.IndexOption ? -1 : 0)
                    .ToStringInvariant(DateFormat.EightCharacter);

                contract.Right = symbol.ID.OptionRight == OptionRight.Call ? IB.RightType.Call : IB.RightType.Put;

                if (symbol.ID.SecurityType == SecurityType.FutureOption)
                {
                    var underlyingContract = CreateContract(symbol.Underlying, includeExpired, exchange);
                    if (underlyingContract == null)
                    {
                        Log.Error($"CreateContract(): Failed to create the underlying future IB contract {symbol}");
                        return null;
                    }
                    contract.Strike = NormalizePriceToBrokerage(symbol.ID.StrikePrice, underlyingContract,
                        symbol.Underlying);
                }
                else
                {
                    contract.Strike = Convert.ToDouble(symbol.ID.StrikePrice);
                }
                contract.Symbol = ibSymbol;
                contract.Multiplier = _symbolPropertiesDatabase.GetSymbolProperties(
                        symbol.ID.Market,
                        symbol,
                        symbol.SecurityType,
                        _algorithm.Portfolio.CashBook.AccountCurrency)
                    .ContractMultiplier
                    .ToStringInvariant();

                contract.TradingClass = GetTradingClass(contract, symbol);
                contract.IncludeExpired = includeExpired;
            }
            if (symbol.ID.SecurityType == SecurityType.Future)
            {
                // we convert Market.* markets into IB exchanges if we have them in our map

                contract.Symbol = ibSymbol;
                contract.LastTradeDateOrContractMonth = symbol.ID.Date.ToStringInvariant(DateFormat.EightCharacter);
                contract.Exchange = GetSymbolExchange(symbol);

                contract.Multiplier = Convert.ToInt32(symbolProperties.ContractMultiplier).ToStringInvariant();

                contract.IncludeExpired = includeExpired;
            }

            return contract;
        }

        /// <summary>
        /// Maps OrderDirection enumeration
        /// </summary>
        private OrderDirection ConvertOrderDirection(string direction)
        {
            switch (direction)
            {
                case IB.ActionSide.Buy: return OrderDirection.Buy;
                case IB.ActionSide.Sell: return OrderDirection.Sell;
                case IB.ActionSide.Undefined: return OrderDirection.Hold;
                default:
                    throw new ArgumentException(direction, nameof(direction));
            }
        }

        /// <summary>
        /// Maps OrderDirection enumeration
        /// </summary>
        private static string ConvertOrderDirection(OrderDirection direction)
        {
            switch (direction)
            {
                case OrderDirection.Buy: return IB.ActionSide.Buy;
                case OrderDirection.Sell: return IB.ActionSide.Sell;
                case OrderDirection.Hold: return IB.ActionSide.Undefined;
                default:
                    throw new InvalidEnumArgumentException(nameof(direction), (int)direction, typeof(OrderDirection));
            }
        }

        /// <summary>
        /// Maps OrderType enum
        /// </summary>
        private static string ConvertOrderType(OrderType type)
        {
            switch (type)
            {
                case OrderType.Market: return IB.OrderType.Market;
                case OrderType.Limit: return IB.OrderType.Limit;
                case OrderType.StopMarket: return IB.OrderType.Stop;
                case OrderType.StopLimit: return IB.OrderType.StopLimit;
                case OrderType.LimitIfTouched: return IB.OrderType.LimitIfTouched;
                case OrderType.MarketOnOpen: return IB.OrderType.Market;
                case OrderType.MarketOnClose: return IB.OrderType.MarketOnClose;
                default:
                    throw new InvalidEnumArgumentException(nameof(type), (int)type, typeof(OrderType));
            }
        }

        /// <summary>
        /// Maps OrderType enum
        /// </summary>
        private static OrderType ConvertOrderType(IBApi.Order order)
        {
            switch (order.OrderType)
            {
                case IB.OrderType.Limit: return OrderType.Limit;
                case IB.OrderType.Stop: return OrderType.StopMarket;
                case IB.OrderType.StopLimit: return OrderType.StopLimit;
                case IB.OrderType.LimitIfTouched: return OrderType.LimitIfTouched;
                case IB.OrderType.MarketOnClose: return OrderType.MarketOnClose;

                case IB.OrderType.Market:
                    if (order.Tif == IB.TimeInForce.MarketOnOpen)
                    {
                        return OrderType.MarketOnOpen;
                    }
                    return OrderType.Market;

                default:
                    throw new ArgumentException(order.OrderType, "order.OrderType");
            }
        }

        /// <summary>
        /// Maps TimeInForce from IB to LEAN
        /// </summary>
        private static TimeInForce ConvertTimeInForce(string timeInForce, string expiryDateTime)
        {
            switch (timeInForce)
            {
                case IB.TimeInForce.Day:
                    return TimeInForce.Day;

                case IB.TimeInForce.GoodTillDate:
                    return TimeInForce.GoodTilDate(ParseExpiryDateTime(expiryDateTime));

                //case IB.TimeInForce.FillOrKill:
                //    return TimeInForce.FillOrKill;

                //case IB.TimeInForce.ImmediateOrCancel:
                //    return TimeInForce.ImmediateOrCancel;

                case IB.TimeInForce.MarketOnOpen:
                case IB.TimeInForce.GoodTillCancel:
                default:
                    return TimeInForce.GoodTilCanceled;
            }
        }

        private static DateTime ParseExpiryDateTime(string expiryDateTime)
        {
            // NOTE: we currently ignore the time zone in this method for a couple of reasons:
            // - TZ abbreviations are ambiguous and unparsable to a unique time zone
            //   see this article for more info:
            //   https://codeblog.jonskeet.uk/2015/05/05/common-mistakes-in-datetime-formatting-and-parsing/
            // - IB seems to also have issues with Daylight Saving Time zones
            //   Example: an order submitted from Europe with GoodTillDate property set to "20180524 21:00:00 UTC"
            //   when reading the open orders, the same property will have this value: "20180524 23:00:00 CET"
            //   which is incorrect: should be CEST (UTC+2) instead of CET (UTC+1)

            // We can ignore this issue, because the method is only called by GetOpenOrders,
            // we only call GetOpenOrders during live trading, which means we won't be simulating time in force
            // and instead will rely on brokerages to apply TIF properly.

            var parts = expiryDateTime.Split(' ');
            if (parts.Length == 3)
            {
                expiryDateTime = expiryDateTime.Substring(0, expiryDateTime.LastIndexOf(" ", StringComparison.Ordinal));
            }

            return DateTime.ParseExact(expiryDateTime, "yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture).Date;
        }

        /// <summary>
        /// Maps TimeInForce from LEAN to IB
        /// </summary>
        private static string ConvertTimeInForce(Order order)
        {
            if (order.Type == OrderType.MarketOnOpen)
            {
                return IB.TimeInForce.MarketOnOpen;
            }
            if (order.Type == OrderType.MarketOnClose)
            {
                return IB.TimeInForce.Day;
            }

            if (order.TimeInForce is DayTimeInForce)
            {
                return IB.TimeInForce.Day;
            }

            if (order.TimeInForce is GoodTilDateTimeInForce)
            {
                return IB.TimeInForce.GoodTillDate;
            }

            //if (order.TimeInForce is FillOrKillTimeInForce)
            //{
            //    return IB.TimeInForce.FillOrKill;
            //}

            //if (order.TimeInForce is ImmediateOrCancelTimeInForce)
            //{
            //    return IB.TimeInForce.ImmediateOrCancel;
            //}

            return IB.TimeInForce.GoodTillCancel;
        }

        /// <summary>
        /// Maps IB's OrderStats enum
        /// </summary>
        private static OrderStatus ConvertOrderStatus(string status)
        {
            switch (status)
            {
                case IB.OrderStatus.ApiPending:
                case IB.OrderStatus.PendingSubmit:
                    return OrderStatus.New;

                case IB.OrderStatus.PendingCancel:
                    return OrderStatus.CancelPending;

                case IB.OrderStatus.ApiCancelled:
                case IB.OrderStatus.Cancelled:
                    return OrderStatus.Canceled;

                case IB.OrderStatus.Submitted:
                case IB.OrderStatus.PreSubmitted:
                    return OrderStatus.Submitted;

                case IB.OrderStatus.Filled:
                    return OrderStatus.Filled;

                case IB.OrderStatus.PartiallyFilled:
                    return OrderStatus.PartiallyFilled;

                case IB.OrderStatus.Error:
                    return OrderStatus.Invalid;

                case IB.OrderStatus.Inactive:
                    Log.Error("InteractiveBrokersBrokerage.ConvertOrderStatus(): Inactive order");
                    return OrderStatus.None;

                case IB.OrderStatus.None:
                    return OrderStatus.None;

                // not sure how to map these guys
                default:
                    throw new ArgumentException(status, nameof(status));
            }
        }

        /// <summary>
        /// Maps SecurityType enum to an IBApi SecurityType value
        /// </summary>
        private static string ConvertSecurityType(SecurityType type)
        {
            switch (type)
            {
                case SecurityType.Equity:
                    return IB.SecurityType.Stock;

                case SecurityType.Option:
                case SecurityType.IndexOption:
                    return IB.SecurityType.Option;

                case SecurityType.Index:
                    return IB.SecurityType.Index;

                case SecurityType.FutureOption:
                    return IB.SecurityType.FutureOption;

                case SecurityType.Forex:
                    return IB.SecurityType.Cash;

                case SecurityType.Future:
                    return IB.SecurityType.Future;

                default:
                    throw new ArgumentException($"The {type} security type is not currently supported.");
            }
        }

        /// <summary>
        /// Maps SecurityType enum
        /// </summary>
        private SecurityType ConvertSecurityType(Contract contract)
        {
            switch (contract.SecType)
            {
                case IB.SecurityType.Stock:
                    return SecurityType.Equity;

                case IB.SecurityType.Option:
                    return IndexOptionSymbol.IsIndexOption(contract.Symbol)
                        ? SecurityType.IndexOption
                        : SecurityType.Option;

                case IB.SecurityType.Index:
                    return SecurityType.Index;

                case IB.SecurityType.FutureOption:
                    return SecurityType.FutureOption;

                case IB.SecurityType.Cash:
                    return SecurityType.Forex;

                case IB.SecurityType.Future:
                    return SecurityType.Future;

                default:
                    throw new NotSupportedException(
                        $"An existing position or open order for an unsupported security type was found: {GetContractDescription(contract)}. " +
                        "Please manually close the position or cancel the order before restarting the algorithm.");
            }
        }

        /// <summary>
        /// Maps Resolution to IB representation
        /// </summary>
        /// <param name="resolution"></param>
        /// <returns></returns>
        private string ConvertResolution(Resolution resolution)
        {
            switch (resolution)
            {
                case Resolution.Tick:
                case Resolution.Second:
                    return IB.BarSize.OneSecond;

                case Resolution.Minute:
                    return IB.BarSize.OneMinute;

                case Resolution.Hour:
                    return IB.BarSize.OneHour;

                case Resolution.Daily:
                default:
                    return IB.BarSize.OneDay;
            }
        }

        /// <summary>
        /// Maps Resolution to IB span
        /// </summary>
        /// <param name="resolution"></param>
        /// <returns></returns>
        private string ConvertResolutionToDuration(Resolution resolution)
        {
            switch (resolution)
            {
                case Resolution.Tick:
                case Resolution.Second:
                    return "1800 S";

                case Resolution.Minute:
                    return "1 D";

                case Resolution.Hour:
                    return "1 M";

                case Resolution.Daily:
                default:
                    return "1 Y";
            }
        }

        private static TradeBar ConvertTradeBar(Symbol symbol, Resolution resolution, IB.HistoricalDataEventArgs historyBar, decimal priceMagnifier)
        {
            var time = resolution != Resolution.Daily ?
                Time.UnixTimeStampToDateTime(Convert.ToDouble(historyBar.Bar.Time, CultureInfo.InvariantCulture)) :
                DateTime.ParseExact(historyBar.Bar.Time, "yyyyMMdd", CultureInfo.InvariantCulture);

            return new TradeBar(time, symbol, (decimal)historyBar.Bar.Open / priceMagnifier, (decimal)historyBar.Bar.High / priceMagnifier,
                (decimal)historyBar.Bar.Low / priceMagnifier, (decimal)historyBar.Bar.Close / priceMagnifier, historyBar.Bar.Volume, resolution.ToTimeSpan());
        }

        /// <summary>
        /// Creates a holding object from the UpdatePortfolioEventArgs
        /// </summary>
        private Holding CreateHolding(IB.UpdatePortfolioEventArgs e)
        {
            var symbol = MapSymbol(e.Contract);

            var currencySymbol = Currencies.GetCurrencySymbol(
                e.Contract.Currency ??
                _symbolPropertiesDatabase.GetSymbolProperties(symbol.ID.Market, symbol, symbol.SecurityType, Currencies.USD).QuoteCurrency);

            var multiplier = e.Contract.Multiplier.ConvertInvariant<decimal>();
            if (multiplier == 0m) multiplier = 1m;

            return new Holding
            {
                Symbol = symbol,
                Quantity = e.Position,
                AveragePrice = Convert.ToDecimal(e.AverageCost) / multiplier,
                MarketPrice = Convert.ToDecimal(e.MarketPrice),
                CurrencySymbol = currencySymbol
            };
        }

        /// <summary>
        /// Maps the IB Contract's symbol to a QC symbol
        /// </summary>
        private Symbol MapSymbol(Contract contract)
        {
            try
            {
                var securityType = ConvertSecurityType(contract);
                var ibSymbol = securityType == SecurityType.Forex ? contract.Symbol + contract.Currency : contract.Symbol;

                var market = InteractiveBrokersBrokerageModel.DefaultMarketMap[securityType];
                var isFutureOption = contract.SecType == IB.SecurityType.FutureOption;

                if (securityType.IsOption() && contract.LastTradeDateOrContractMonth == "0")
                {
                    // Try our best to recover from a malformed contract.
                    // You can read more about malformed contracts at the ParseMalformedContract method's documentation.
                    var exchange = GetSymbolExchange(securityType, market);

                    contract = InteractiveBrokersSymbolMapper.ParseMalformedContractOptionSymbol(contract, exchange);
                    ibSymbol = contract.Symbol;
                }
                else if (securityType == SecurityType.Future && contract.LastTradeDateOrContractMonth == "0")
                {
                    contract = _symbolMapper.ParseMalformedContractFutureSymbol(contract, _symbolPropertiesDatabase);
                    ibSymbol = contract.Symbol;
                }

                // Handle future options as a Future, up until we actually return the future.
                if (isFutureOption || securityType == SecurityType.Future)
                {
                    var leanSymbol = _symbolMapper.GetLeanRootSymbol(ibSymbol);
                    var defaultMarket = market;

                    if (!_symbolPropertiesDatabase.TryGetMarket(leanSymbol, SecurityType.Future, out market))
                    {
                        market = defaultMarket;
                    }

                    var contractExpiryDate = DateTime.ParseExact(contract.LastTradeDateOrContractMonth, DateFormat.EightCharacter, CultureInfo.InvariantCulture);

                    if (!isFutureOption)
                    {
                        return _symbolMapper.GetLeanSymbol(ibSymbol, SecurityType.Future, market, contractExpiryDate);
                    }

                    // Get the *actual* futures contract that this futures options contract has as its underlying.
                    // Futures options contracts can have a different contract month from their underlying future.
                    // As such, we resolve the underlying future to the future with the correct contract month.
                    // There's a chance this can fail, and if it does, we throw because this Symbol can't be
                    // represented accurately in Lean.
                    var futureSymbol = FuturesOptionsUnderlyingMapper.GetUnderlyingFutureFromFutureOption(leanSymbol, market, contractExpiryDate, _algorithm.Time);
                    if (futureSymbol == null)
                    {
                        // This is the worst case scenario, because we didn't find a matching futures contract for the FOP.
                        // Note that this only applies to CBOT symbols for now.
                        throw new ArgumentException($"The Future Option contract: {GetContractDescription(contract)} with trading class: {contract.TradingClass} has no matching underlying future contract.");
                    }

                    var right = contract.Right == IB.RightType.Call ? OptionRight.Call : OptionRight.Put;
                    // we don't have the Lean ticker yet, ticker is just used for logging
                    var strike = NormalizePriceToLean(contract.Strike, futureSymbol);

                    return Symbol.CreateOption(futureSymbol, market, OptionStyle.American, right, strike, contractExpiryDate);
                }

                if (securityType.IsOption())
                {
                    var expiryDate = DateTime.ParseExact(contract.LastTradeDateOrContractMonth, DateFormat.EightCharacter, CultureInfo.InvariantCulture);
                    var right = contract.Right == IB.RightType.Call ? OptionRight.Call : OptionRight.Put;
                    var strike = Convert.ToDecimal(contract.Strike);

                    return _symbolMapper.GetLeanSymbol(ibSymbol, securityType, market, expiryDate, strike, right);
                }

                return _symbolMapper.GetLeanSymbol(ibSymbol, securityType, market);
            }
            catch (Exception error)
            {
                throw new Exception($"InteractiveBrokersBrokerage.MapSymbol(): Failed to convert contract for {contract.Symbol}; Contract description: {GetContractDescription(contract)}", error);
            }
        }

        private static decimal RoundPrice(decimal input, decimal minTick)
        {
            if (minTick == 0) return minTick;
            return Math.Round(input / minTick) * minTick;
        }

        /// <summary>
        /// Handles the threading issues of creating an IB OrderId/RequestId/TickerId
        /// </summary>
        /// <returns>The new IB OrderId/RequestId/TickerId</returns>
        private int GetNextId()
        {
            lock (_nextValidIdLocker)
            {
                // return the current value and increment
                return _nextValidId++;
            }
        }

        private void HandleBrokerTime(object sender, IB.CurrentTimeUtcEventArgs e)
        {
            _currentTimeEvent.Set();
        }

        /// <summary>
        /// Sets the job we're subscribing for
        /// </summary>
        /// <param name="job">Job we're subscribing for</param>
        public void SetJob(LiveNodePacket job)
        {
            // read values from the brokerage datas
            var port = Config.GetInt("ib-port", 4001);
            var host = Config.Get("ib-host", "127.0.0.1");
            var twsDirectory = Config.Get("ib-tws-dir", "C:\\Jts");
            var ibVersion = Config.Get("ib-version", DefaultVersion);

            var account = job.BrokerageData["ib-account"];
            var userId = job.BrokerageData["ib-user-name"];
            var password = job.BrokerageData["ib-password"];
            var tradingMode = job.BrokerageData["ib-trading-mode"];
            var agentDescription = job.BrokerageData["ib-agent-description"];

            var loadExistingHoldings = true;
            if (job.BrokerageData.ContainsKey("load-existing-holdings"))
            {
                loadExistingHoldings = Convert.ToBoolean(job.BrokerageData["load-existing-holdings"]);
            }

            Initialize(null,
                null,
                null,
                Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"), forceTypeNameOnExisting: false),
                Composer.Instance.GetExportedValueByTypeName<IMapFileProvider>(Config.Get("map-file-provider", "QuantConnect.Data.Auxiliary.LocalDiskMapFileProvider")),
                account,
                host,
                port,
                twsDirectory,
                ibVersion,
                userId,
                password,
                tradingMode,
                agentDescription,
                loadExistingHoldings);

            if (!IsConnected)
            {
                Connect();
            }
        }

        /// <summary>
        /// Subscribe to the specified configuration
        /// </summary>
        /// <param name="dataConfig">defines the parameters to subscribe to a data feed</param>
        /// <param name="newDataAvailableHandler">handler to be fired on new data available</param>
        /// <returns>The new enumerator for this subscription request</returns>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            _subscriptionManager.Subscribe(dataConfig);

            return enumerator;
        }

        /// <summary>
        /// Adds the specified symbols to the subscription
        /// </summary>
        /// <param name="symbols">The symbols to be added keyed by SecurityType</param>
        private bool Subscribe(IEnumerable<Symbol> symbols)
        {
            try
            {
                foreach (var symbol in symbols)
                {
                    lock (_sync)
                    {
                        Log.Trace("InteractiveBrokersBrokerage.Subscribe(): Subscribe Request: " + symbol.Value);

                        if (!_subscribedSymbols.ContainsKey(symbol))
                        {
                            // processing canonical option and futures symbols
                            var subscribeSymbol = symbol;

                            // we subscribe to the underlying
                            if (symbol.ID.SecurityType.IsOption() && symbol.IsCanonical())
                            {
                                subscribeSymbol = symbol.Underlying;
                                _underlyings.Add(subscribeSymbol, symbol);
                            }

                            // we ignore futures canonical symbol
                            if (symbol.ID.SecurityType == SecurityType.Future && symbol.IsCanonical())
                            {
                                continue;
                            }

                            var id = GetNextId();
                            var contract = CreateContract(subscribeSymbol, false);
                            var symbolProperties = _symbolPropertiesDatabase.GetSymbolProperties(subscribeSymbol.ID.Market, subscribeSymbol, subscribeSymbol.SecurityType, Currencies.USD);
                            var priceMagnifier = symbolProperties.PriceMagnifier;

                            _requestInformation[id] = $"[Id={id}] Subscribe: {symbol.Value} ({GetContractDescription(contract)})";

                            CheckRateLimiting();

                            // track subscription time for minimum delay in unsubscribe
                            _subscriptionTimes[id] = DateTime.UtcNow;

                            if (_enableDelayedStreamingData)
                            {
                                // Switch to delayed market data if the user does not have the necessary real time data subscription.
                                // If live data is available, it will always be returned instead of delayed data.
                                Client.ClientSocket.reqMarketDataType(3);
                            }

                            // we would like to receive OI (101)
                            Client.ClientSocket.reqMktData(id, contract, "101", false, false, new List<TagValue>());

                            _subscribedSymbols[symbol] = id;
                            _subscribedTickers[id] = new SubscriptionEntry { Symbol = subscribeSymbol, PriceMagnifier = priceMagnifier };

                            Log.Trace($"InteractiveBrokersBrokerage.Subscribe(): Subscribe Processed: {symbol.Value} ({GetContractDescription(contract)}) # {id}");
                        }
                    }
                }
                return true;
            }
            catch (Exception err)
            {
                Log.Error("InteractiveBrokersBrokerage.Subscribe(): " + err.Message);
            }
            return false;
        }

        /// <summary>
        /// Removes the specified configuration
        /// </summary>
        /// <param name="dataConfig">Subscription config to be removed</param>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscriptionManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
        }

        /// <summary>
        /// Removes the specified symbols to the subscription
        /// </summary>
        /// <param name="symbols">The symbols to be removed keyed by SecurityType</param>
        private bool Unsubscribe(IEnumerable<Symbol> symbols)
        {
            try
            {
                foreach (var symbol in symbols)
                {
                    if (CanSubscribe(symbol))
                    {
                        lock (_sync)
                        {
                            Log.Trace("InteractiveBrokersBrokerage.Unsubscribe(): Unsubscribe Request: " + symbol.Value);

                            if (symbol.ID.SecurityType.IsOption() && symbol.ID.StrikePrice == 0.0m)
                            {
                                _underlyings.Remove(symbol.Underlying);
                            }

                            int id;
                            if (_subscribedSymbols.TryRemove(symbol, out id))
                            {
                                CheckRateLimiting();

                                // ensure minimum time span has elapsed since the symbol was subscribed
                                DateTime subscriptionTime;
                                if (_subscriptionTimes.TryGetValue(id, out subscriptionTime))
                                {
                                    var timeSinceSubscription = DateTime.UtcNow - subscriptionTime;
                                    if (timeSinceSubscription < _minimumTimespanBeforeUnsubscribe)
                                    {
                                        var delay = Convert.ToInt32((_minimumTimespanBeforeUnsubscribe - timeSinceSubscription).TotalMilliseconds);
                                        Thread.Sleep(delay);
                                    }

                                    _subscriptionTimes.Remove(id);
                                }

                                Client.ClientSocket.cancelMktData(id);

                                SubscriptionEntry entry;
                                return _subscribedTickers.TryRemove(id, out entry);
                            }
                        }
                    }
                }
            }
            catch (Exception err)
            {
                Log.Error("InteractiveBrokersBrokerage.Unsubscribe(): " + err.Message);
            }
            return false;
        }

        /// <summary>
        /// Returns true if this data provide can handle the specified symbol
        /// </summary>
        /// <param name="symbol">The symbol to be handled</param>
        /// <returns>True if this data provider can get data for the symbol, false otherwise</returns>
        private static bool CanSubscribe(Symbol symbol)
        {
            var market = symbol.ID.Market;
            var securityType = symbol.ID.SecurityType;

            if (symbol.Value.IndexOfInvariant("universe", true) != -1
                // continuous futures and canonical symbols not supported
                || symbol.IsCanonical())
            {
                return false;
            }

            // Include future options as a special case with no matching market, otherwise
            // our subscriptions are removed without any sort of notice.
            return
                (securityType == SecurityType.Equity && market == Market.USA) ||
                (securityType == SecurityType.Forex && market == Market.Oanda) ||
                (securityType == SecurityType.Option && market == Market.USA) ||
                (securityType == SecurityType.IndexOption && market == Market.USA) ||
                (securityType == SecurityType.Index && market == Market.USA) ||
                (securityType == SecurityType.FutureOption) ||
                (securityType == SecurityType.Future);
        }

        /// <summary>
        /// Returns a timestamp for a tick converted to the exchange time zone
        /// </summary>
        private DateTime GetRealTimeTickTime(Symbol symbol)
        {
            var time = DateTime.UtcNow;

            DateTimeZone exchangeTimeZone;
            if (!_symbolExchangeTimeZones.TryGetValue(symbol, out exchangeTimeZone))
            {
                // read the exchange time zone from market-hours-database
                exchangeTimeZone = MarketHoursDatabase.FromDataFolder().GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType).TimeZone;
                _symbolExchangeTimeZones.Add(symbol, exchangeTimeZone);
            }

            return time.ConvertFromUtc(exchangeTimeZone);
        }

        private void HandleTickPrice(object sender, IB.TickPriceEventArgs e)
        {
            // tickPrice events are always followed by tickSize events,
            // so we save off the bid/ask/last prices and only emit ticks in the tickSize event handler.

            SubscriptionEntry entry;
            if (!_subscribedTickers.TryGetValue(e.TickerId, out entry))
            {
                return;
            }

            var symbol = entry.Symbol;

            // negative price (-1) means no price available, normalize to zero
            var price = e.Price < 0 ? 0 : Convert.ToDecimal(e.Price) / entry.PriceMagnifier;

            switch (e.Field)
            {
                case IBApi.TickType.BID:
                case IBApi.TickType.DELAYED_BID:

                    if (entry.LastQuoteTick == null)
                    {
                        entry.LastQuoteTick = new Tick
                        {
                            // in the event of a symbol change this will break since we'll be assigning the
                            // new symbol to the permtick which won't be known by the algorithm
                            Symbol = symbol,
                            TickType = TickType.Quote
                        };
                    }

                    // set the last bid price
                    entry.LastQuoteTick.BidPrice = price;
                    break;

                case IBApi.TickType.ASK:
                case IBApi.TickType.DELAYED_ASK:

                    if (entry.LastQuoteTick == null)
                    {
                        entry.LastQuoteTick = new Tick
                        {
                            // in the event of a symbol change this will break since we'll be assigning the
                            // new symbol to the permtick which won't be known by the algorithm
                            Symbol = symbol,
                            TickType = TickType.Quote
                        };
                    }

                    // set the last ask price
                    entry.LastQuoteTick.AskPrice = price;
                    break;

                case IBApi.TickType.LAST:
                case IBApi.TickType.DELAYED_LAST:

                    if (entry.LastTradeTick == null)
                    {
                        entry.LastTradeTick = new Tick
                        {
                            // in the event of a symbol change this will break since we'll be assigning the
                            // new symbol to the permtick which won't be known by the algorithm
                            Symbol = symbol,
                            TickType = TickType.Trade
                        };
                    }

                    // set the last traded price
                    entry.LastTradeTick.Value = price;
                    break;

                default:
                    return;
            }
        }

        /// <summary>
        /// Modifies the quantity received from IB based on the security type
        /// </summary>
        public int AdjustQuantity(SecurityType type, int size)
        {
            switch (type)
            {
                case SecurityType.Equity:
                    // Effective in TWS version 985 and later, for US stocks the bid, ask, and last size quotes are shown in shares (not in lots).
                    return _ibVersion < 985 ? size * 100 : size;

                default:
                    return size;
            }
        }

        private void HandleTickSize(object sender, IB.TickSizeEventArgs e)
        {
            SubscriptionEntry entry;
            if (!_subscribedTickers.TryGetValue(e.TickerId, out entry))
            {
                return;
            }

            var symbol = entry.Symbol;

            var securityType = symbol.ID.SecurityType;

            // negative size (-1) means no quantity available, normalize to zero
            var quantity = e.Size < 0 ? 0 : AdjustQuantity(securityType, e.Size);

            Tick tick;
            switch (e.Field)
            {
                case IBApi.TickType.BID_SIZE:
                case IBApi.TickType.DELAYED_BID_SIZE:

                    tick = entry.LastQuoteTick;

                    if (tick == null)
                    {
                        // tick size message must be preceded by a tick price message
                        return;
                    }

                    tick.BidSize = quantity;

                    if (tick.BidPrice == 0)
                    {
                        // no bid price, do not emit tick
                        return;
                    }

                    if (tick.BidPrice > 0 && tick.AskPrice > 0 && tick.BidPrice >= tick.AskPrice)
                    {
                        // new bid price jumped at or above previous ask price, wait for new ask price
                        return;
                    }

                    if (tick.AskPrice == 0)
                    {
                        // we have a bid price but no ask price, use bid price as value
                        tick.Value = tick.BidPrice;
                    }
                    else
                    {
                        // we have both bid price and ask price, use mid price as value
                        tick.Value = (tick.BidPrice + tick.AskPrice) / 2;
                    }
                    break;

                case IBApi.TickType.ASK_SIZE:
                case IBApi.TickType.DELAYED_ASK_SIZE:

                    tick = entry.LastQuoteTick;

                    if (tick == null)
                    {
                        // tick size message must be preceded by a tick price message
                        return;
                    }

                    tick.AskSize = quantity;

                    if (tick.AskPrice == 0)
                    {
                        // no ask price, do not emit tick
                        return;
                    }

                    if (tick.BidPrice > 0 && tick.AskPrice > 0 && tick.BidPrice >= tick.AskPrice)
                    {
                        // new ask price jumped at or below previous bid price, wait for new bid price
                        return;
                    }

                    if (tick.BidPrice == 0)
                    {
                        // we have an ask price but no bid price, use ask price as value
                        tick.Value = tick.AskPrice;
                    }
                    else
                    {
                        // we have both bid price and ask price, use mid price as value
                        tick.Value = (tick.BidPrice + tick.AskPrice) / 2;
                    }
                    break;

                case IBApi.TickType.LAST_SIZE:
                case IBApi.TickType.DELAYED_LAST_SIZE:

                    tick = entry.LastTradeTick;

                    if (tick == null)
                    {
                        // tick size message must be preceded by a tick price message
                        return;
                    }

                    // set the traded quantity
                    tick.Quantity = quantity;
                    break;

                case IBApi.TickType.OPEN_INTEREST:
                case IBApi.TickType.OPTION_CALL_OPEN_INTEREST:
                case IBApi.TickType.OPTION_PUT_OPEN_INTEREST:

                    if (!symbol.ID.SecurityType.IsOption() && symbol.ID.SecurityType != SecurityType.Future)
                    {
                        return;
                    }

                    if (entry.LastOpenInterestTick == null)
                    {
                        entry.LastOpenInterestTick = new Tick { Symbol = symbol, TickType = TickType.OpenInterest };
                    }

                    tick = entry.LastOpenInterestTick;

                    tick.Value = e.Size;
                    break;

                default:
                    return;
            }

            if (tick.IsValid())
            {
                tick = new Tick(tick)
                {
                    Time = GetRealTimeTickTime(symbol)
                };

                _aggregator.Update(tick);

                if (_underlyings.ContainsKey(tick.Symbol))
                {
                    var underlyingTick = tick.Clone() as Tick;
                    underlyingTick.Symbol = _underlyings[tick.Symbol];
                    _aggregator.Update(underlyingTick);
                }
            }
        }

        /// <summary>
        /// Method returns a collection of Symbols that are available at the broker.
        /// </summary>
        /// <param name="symbol">Symbol to search future/option chain for</param>
        /// <param name="includeExpired">Include expired contracts</param>
        /// <param name="securityCurrency">Expected security currency(if any)</param>
        /// <returns>Future/Option chain associated with the Symbol provided</returns>
        public IEnumerable<Symbol> LookupSymbols(Symbol symbol, bool includeExpired, string securityCurrency = null)
        {
            // setting up exchange defaults and filters
            var exchangeSpecifier = GetSymbolExchange(symbol);
            var futuresExchanges = _futuresExchanges.Values.Reverse().ToArray();
            Func<string, int> exchangeFilter = exchange => symbol.SecurityType == SecurityType.Future ? Array.IndexOf(futuresExchanges, exchange) : 0;

            var lookupName = symbol.Value;

            if (symbol.SecurityType == SecurityType.Future)
            {
                lookupName = symbol.ID.Symbol;
            }
            else if (symbol.SecurityType == SecurityType.Option || symbol.SecurityType == SecurityType.IndexOption)
            {
                lookupName = symbol.Underlying.Value;
            }
            else if (symbol.SecurityType == SecurityType.FutureOption)
            {
                // Futures Options use the underlying Symbol ticker for their ticker on IB.
                lookupName = symbol.Underlying.ID.Symbol;
            }

            var symbolProperties = _symbolPropertiesDatabase.GetSymbolProperties(
                        symbol.ID.Market,
                        symbol,
                        symbol.SecurityType,
                        _algorithm.Portfolio.CashBook.AccountCurrency);

            // setting up lookup request
            var contract = new Contract
            {
                Symbol = _symbolMapper.GetBrokerageRootSymbol(lookupName),
                Currency = securityCurrency ?? symbolProperties.QuoteCurrency,
                Exchange = exchangeSpecifier,
                SecType = ConvertSecurityType(symbol.SecurityType),
                IncludeExpired = includeExpired,
                Multiplier = Convert.ToInt32(symbolProperties.ContractMultiplier).ToStringInvariant()
            };

            Log.Trace($"InteractiveBrokersBrokerage.LookupSymbols(): Requesting symbol list for {contract.Symbol} ...");

            var symbols = new List<Symbol>();

            if (symbol.SecurityType.IsOption())
            {
                // IB requests for full option chains are rate limited and responses can be delayed up to a minute for each underlying,
                // so we fetch them from the OCC website instead of using the IB API.
                // For futures options, we fetch the option chain from CME.
                symbols.AddRange(_algorithm.OptionChainProvider.GetOptionContractList(symbol.Underlying, DateTime.Today));
            }
            else if (symbol.SecurityType == SecurityType.Future)
            {
                // processing request
                var results = FindContracts(contract, contract.Symbol);

                // filtering results
                var filteredResults =
                    results
                        .Select(x => x.Contract)
                        .GroupBy(x => x.Exchange)
                        .OrderByDescending(g => exchangeFilter(g.Key))
                        .FirstOrDefault();

                if (filteredResults != null)
                {
                    symbols.AddRange(filteredResults.Select(MapSymbol));
                }
            }

            // Try to remove options or futures contracts that have expired
            if (!includeExpired)
            {
                if (symbol.SecurityType.IsOption() || symbol.SecurityType == SecurityType.Future)
                {
                    var removedSymbols = symbols.Where(x => x.ID.Date < GetRealTimeTickTime(x).Date).ToHashSet();

                    if (symbols.RemoveAll(x => removedSymbols.Contains(x)) > 0)
                    {
                        Log.Trace("InteractiveBrokersBrokerage.LookupSymbols(): Removed contract(s) for having expiry in the past: {0}", string.Join(",", removedSymbols.Select(x => x.Value)));
                    }
                }
            }

            Log.Trace($"InteractiveBrokersBrokerage.LookupSymbols(): Returning {symbols.Count} contract(s) for {contract.Symbol}");

            return symbols;
        }

        /// <summary>
        /// Returns whether selection can take place or not.
        /// </summary>
        /// <returns>True if selection can take place</returns>
        public bool CanPerformSelection()
        {
            return !_ibAutomater.IsWithinScheduledServerResetTimes() && IsConnected;
        }

        /// <summary>
        /// Gets the history for the requested security
        /// </summary>
        /// <param name="request">The historical data request</param>
        /// <returns>An enumerable of bars covering the span specified in the request</returns>
        /// <remarks>For IB history limitations see https://www.interactivebrokers.com/en/software/api/apiguide/tables/historical_data_limitations.htm </remarks>
        public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            if (!IsConnected)
            {
                OnMessage(
                    new BrokerageMessageEvent(
                        BrokerageMessageType.Warning,
                        "GetHistoryWhenDisconnected",
                        "History requests cannot be submitted when disconnected."));
                yield break;
            }

            // skipping universe and canonical symbols
            if (!CanSubscribe(request.Symbol) ||
                (request.Symbol.ID.SecurityType.IsOption() && request.Symbol.IsCanonical()))
            {
                yield break;
            }

            // skip invalid security types
            if (request.Symbol.SecurityType != SecurityType.Equity &&
                request.Symbol.SecurityType != SecurityType.Index &&
                request.Symbol.SecurityType != SecurityType.Forex &&
                request.Symbol.SecurityType != SecurityType.Cfd &&
                request.Symbol.SecurityType != SecurityType.Future &&
                request.Symbol.SecurityType != SecurityType.FutureOption &&
                request.Symbol.SecurityType != SecurityType.Option &&
                request.Symbol.SecurityType != SecurityType.IndexOption)
            {
                yield break;
            }

            // tick resolution not supported for now
            if (request.Resolution == Resolution.Tick)
            {
                // TODO: upgrade IB C# API DLL
                // In IB API version 973.04, the reqHistoricalTicks function has been added,
                // which would now enable us to support history requests at Tick resolution.
                yield break;
            }

            // preparing the data for IB request
            var contract = CreateContract(request.Symbol, true);
            var resolution = ConvertResolution(request.Resolution);
            var duration = ConvertResolutionToDuration(request.Resolution);
            var startTime = request.Resolution == Resolution.Daily ? request.StartTimeUtc.Date : request.StartTimeUtc;
            var endTime = request.Resolution == Resolution.Daily ? request.EndTimeUtc.Date : request.EndTimeUtc;

            Log.Trace($"InteractiveBrokersBrokerage::GetHistory(): Submitting request: {request.Symbol.Value} ({GetContractDescription(contract)}): {request.Resolution}/{request.TickType} {startTime} UTC -> {endTime} UTC");

            DateTimeZone exchangeTimeZone;
            if (!_symbolExchangeTimeZones.TryGetValue(request.Symbol, out exchangeTimeZone))
            {
                // read the exchange time zone from market-hours-database
                exchangeTimeZone = MarketHoursDatabase.FromDataFolder().GetExchangeHours(request.Symbol.ID.Market, request.Symbol, request.Symbol.SecurityType).TimeZone;
                _symbolExchangeTimeZones.Add(request.Symbol, exchangeTimeZone);
            }

            IEnumerable<BaseData> history;
            if (request.TickType == TickType.Quote)
            {
                // Quotes need two separate IB requests for Bid and Ask,
                // each pair of TradeBars will be joined into a single QuoteBar
                var historyBid = GetHistory(request, contract, startTime, endTime, exchangeTimeZone, duration, resolution, HistoricalDataType.Bid);
                var historyAsk = GetHistory(request, contract, startTime, endTime, exchangeTimeZone, duration, resolution, HistoricalDataType.Ask);

                history = historyBid.Join(historyAsk,
                    bid => bid.Time,
                    ask => ask.Time,
                    (bid, ask) => new QuoteBar(
                        bid.Time,
                        bid.Symbol,
                        new Bar(bid.Open, bid.High, bid.Low, bid.Close),
                        0,
                        new Bar(ask.Open, ask.High, ask.Low, ask.Close),
                        0,
                        bid.Period));
            }
            else
            {
                // other assets will have TradeBars
                history = GetHistory(request, contract, startTime, endTime, exchangeTimeZone, duration, resolution, HistoricalDataType.Trades);
            }

            // cleaning the data before returning it back to user
            var requestStartTime = request.StartTimeUtc.ConvertFromUtc(exchangeTimeZone);
            var requestEndTime = request.EndTimeUtc.ConvertFromUtc(exchangeTimeZone);

            foreach (var bar in history.Where(bar => bar.Time >= requestStartTime && bar.EndTime <= requestEndTime))
            {
                if (request.Symbol.SecurityType == SecurityType.Equity ||
                    request.ExchangeHours.IsOpen(bar.Time, bar.EndTime, request.IncludeExtendedMarketHours))
                {
                    yield return bar;
                }
            }

            Log.Trace($"InteractiveBrokersBrokerage::GetHistory(): Download completed: {request.Symbol.Value} ({GetContractDescription(contract)})");
        }

        private IEnumerable<TradeBar> GetHistory(
            HistoryRequest request,
            Contract contract,
            DateTime startTime,
            DateTime endTime,
            DateTimeZone exchangeTimeZone,
            string duration,
            string resolution,
            string dataType)
        {
            const int timeOut = 60; // seconds timeout

            var history = new List<TradeBar>();
            var dataDownloading = new AutoResetEvent(false);
            var dataDownloaded = new AutoResetEvent(false);

            // This is needed because when useRTH is set to 1, IB will return data only
            // during Equity regular trading hours (for any asset type, not only for equities)
            var useRegularTradingHours = request.Symbol.SecurityType == SecurityType.Equity
                ? Convert.ToInt32(!request.IncludeExtendedMarketHours)
                : 0;

            var symbolProperties = _symbolPropertiesDatabase.GetSymbolProperties(request.Symbol.ID.Market, request.Symbol, request.Symbol.SecurityType, Currencies.USD);
            var priceMagnifier = symbolProperties.PriceMagnifier;

            // making multiple requests if needed in order to download the history
            while (endTime >= startTime)
            {
                var pacing = false;
                var historyPiece = new List<TradeBar>();
                var historicalTicker = GetNextId();

                _requestInformation[historicalTicker] = $"[Id={historicalTicker}] GetHistory: {request.Symbol.Value} ({GetContractDescription(contract)})";

                EventHandler<IB.HistoricalDataEventArgs> clientOnHistoricalData = (sender, args) =>
                {
                    if (args.RequestId == historicalTicker)
                    {
                        var bar = ConvertTradeBar(request.Symbol, request.Resolution, args, priceMagnifier);
                        if (request.Resolution != Resolution.Daily)
                        {
                            bar.Time = bar.Time.ConvertFromUtc(exchangeTimeZone);
                        }

                        historyPiece.Add(bar);
                        dataDownloading.Set();
                    }
                };

                EventHandler<IB.HistoricalDataEndEventArgs> clientOnHistoricalDataEnd = (sender, args) =>
                {
                    if (args.RequestId == historicalTicker)
                    {
                        dataDownloaded.Set();
                    }
                };

                EventHandler<IB.ErrorEventArgs> clientOnError = (sender, args) =>
                {
                    if (args.Id == historicalTicker)
                    {
                        if (args.Code == 162 && args.Message.Contains("pacing violation"))
                        {
                            // pacing violation happened
                            pacing = true;
                        }
                        else
                        {
                            dataDownloaded.Set();
                        }
                    }
                };

                Client.Error += clientOnError;
                Client.HistoricalData += clientOnHistoricalData;
                Client.HistoricalDataEnd += clientOnHistoricalDataEnd;

                CheckRateLimiting();

                Client.ClientSocket.reqHistoricalData(historicalTicker, contract, endTime.ToStringInvariant("yyyyMMdd HH:mm:ss UTC"),
                    duration, resolution, dataType, useRegularTradingHours, 2, false, new List<TagValue>());

                var waitResult = 0;
                while (waitResult == 0)
                {
                    waitResult = WaitHandle.WaitAny(new WaitHandle[] { dataDownloading, dataDownloaded }, timeOut * 1000);
                }

                Client.Error -= clientOnError;
                Client.HistoricalData -= clientOnHistoricalData;
                Client.HistoricalDataEnd -= clientOnHistoricalDataEnd;

                if (waitResult == WaitHandle.WaitTimeout)
                {
                    if (pacing)
                    {
                        // we received 'pacing violation' error from IB. So we had to wait
                        Log.Trace("InteractiveBrokersBrokerage::GetHistory() Pacing violation. Paused for {0} secs.", timeOut);
                        continue;
                    }

                    Log.Trace("InteractiveBrokersBrokerage::GetHistory() History request timed out ({0} sec)", timeOut);
                    break;
                }

                // if no data has been received this time, we exit
                if (!historyPiece.Any())
                {
                    break;
                }

                var filteredPiece = historyPiece.OrderBy(x => x.Time);

                history.InsertRange(0, filteredPiece);

                // moving endTime to the new position to proceed with next request (if needed)
                endTime = filteredPiece.First().Time.ConvertToUtc(exchangeTimeZone);
            }

            return history;
        }

        /// <summary>
        /// Gets the exchange the Symbol should be routed to
        /// </summary>
        /// <param name="securityType">SecurityType of the Symbol</param>
        /// <param name="market">Market of the Symbol</param>
        /// <param name="ticker">Ticker for the symbol</param>
        private string GetSymbolExchange(SecurityType securityType, string market, string ticker = null)
        {
            switch (securityType)
            {
                case SecurityType.Option:
                case SecurityType.IndexOption:
                    // Regular equity options uses default, in this case "Smart"
                    goto default;

                // Futures options share the same market as the underlying Symbol
                case SecurityType.FutureOption:
                case SecurityType.Future:
                    return _futuresExchanges.ContainsKey(market)
                        ? ticker == "BTC"
                            ? _futuresCmeCrypto
                            : _futuresExchanges[market]
                        : market;

                default:
                    return "Smart";
            }
        }

        /// <summary>
        /// Gets the exchange the Symbol should be routed to
        /// </summary>
        /// <param name="symbol">Symbol to route</param>
        private string GetSymbolExchange(Symbol symbol)
        {
            return GetSymbolExchange(symbol.SecurityType, symbol.ID.Market, symbol.ID.Symbol);
        }

        /// <summary>
        /// Returns whether the brokerage should perform the cash synchronization
        /// </summary>
        /// <param name="currentTimeUtc">The current time (UTC)</param>
        /// <returns>True if the cash sync should be performed</returns>
        public override bool ShouldPerformCashSync(DateTime currentTimeUtc)
        {
            return base.ShouldPerformCashSync(currentTimeUtc)
                && !_ibAutomater.IsWithinScheduledServerResetTimes()
                && IsConnected;
        }

        private void CheckRateLimiting()
        {
            if (!_messagingRateLimiter.WaitToProceed(TimeSpan.Zero))
            {
                Log.Trace("The IB API request has been rate limited.");

                _messagingRateLimiter.WaitToProceed();
            }
        }

        private void OnIbAutomaterOutputDataReceived(object sender, OutputDataReceivedEventArgs e)
        {
            if (e.Data == null) return;

            Log.Trace($"InteractiveBrokersBrokerage.OnIbAutomaterOutputDataReceived(): {e.Data}");
        }

        private void OnIbAutomaterErrorDataReceived(object sender, ErrorDataReceivedEventArgs e)
        {
            if (e.Data == null) return;

            Log.Trace($"InteractiveBrokersBrokerage.OnIbAutomaterErrorDataReceived(): {e.Data}");
        }

        private void OnIbAutomaterExited(object sender, ExitedEventArgs e)
        {
            Log.Trace($"InteractiveBrokersBrokerage.OnIbAutomaterExited(): Exit code: {e.ExitCode}");

            _stateManager.Reset();

            // check if IBGateway was closed because of an IBAutomater error
            var result = _ibAutomater.GetLastStartResult();
            CheckIbAutomaterError(result, false);

            if (!result.HasError && !_isDisposeCalled)
            {
                // IBGateway was closed by IBAutomater because the auto-restart token expired or it was closed manually (less likely)
                Log.Trace("InteractiveBrokersBrokerage.OnIbAutomaterExited(): IBGateway close detected, restarting IBAutomater...");

                try
                {
                    // disconnect immediately so orders will not be submitted to the API while waiting for reconnection
                    Disconnect();
                }
                catch (Exception exception)
                {
                    Log.Trace($"InteractiveBrokersBrokerage.OnIbAutomaterExited(): error in Disconnect(): {exception}");
                }

                // during weekends wait until one hour before FX market open before restarting IBAutomater
                var delay = _ibAutomater.IsWithinWeekendServerResetTimes()
                    ? GetNextWeekendReconnectionTimeUtc() - DateTime.UtcNow
                    : TimeSpan.FromMinutes(5);

                Log.Trace($"InteractiveBrokersBrokerage.OnIbAutomaterExited(): Delay before restart: {delay:d'd 'h'h 'm'm 's's'}");

                Task.Delay(delay).ContinueWith(_ =>
                {
                    try
                    {
                        Log.Trace("InteractiveBrokersBrokerage.OnIbAutomaterExited(): restarting...");

                        CheckIbAutomaterError(_ibAutomater.Start(false));

                        Connect();
                    }
                    catch (Exception exception)
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "IBAutomaterRestartError", exception.ToString()));
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        private void OnIbAutomaterRestarted(object sender, EventArgs e)
        {
            Log.Trace("InteractiveBrokersBrokerage.OnIbAutomaterRestarted()");

            _stateManager.Reset();

            // check if IBGateway was closed because of an IBAutomater error
            var result = _ibAutomater.GetLastStartResult();
            CheckIbAutomaterError(result, false);

            if (!result.HasError && !_isDisposeCalled)
            {
                // IBGateway was restarted automatically
                Log.Trace("InteractiveBrokersBrokerage.OnIbAutomaterRestarted(): IBGateway restart detected, reconnecting...");

                try
                {
                    Disconnect();

                    Connect();
                }
                catch (Exception exception)
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "IBAutomaterAutoRestartError", exception.ToString()));
                }
            }
        }

        /// <summary>
        /// Gets the time (UTC) of the next reconnection attempt.
        /// </summary>
        private static DateTime GetNextWeekendReconnectionTimeUtc()
        {
            // return the UTC time at one hour before Sunday FX market open,
            // ignoring holidays as we should be able to connect with closed markets anyway
            var nextDate = DateTime.UtcNow.ConvertFromUtc(TimeZones.NewYork).Date;
            while (nextDate.DayOfWeek != DayOfWeek.Sunday)
            {
                nextDate += Time.OneDay;
            }

            return new DateTime(nextDate.Year, nextDate.Month, nextDate.Day, 16, 0, 0).ConvertToUtc(TimeZones.NewYork);
        }

        private void CheckIbAutomaterError(StartResult result, bool throwException = true)
        {
            if (result.HasError)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, result.ErrorCode.ToString(), result.ErrorMessage));

                if (throwException)
                {
                    throw new Exception($"InteractiveBrokersBrokerage.CheckIbAutomaterError(): {result.ErrorCode} - {result.ErrorMessage}");
                }
            }
        }

        private void HandleAccountSummary(object sender, IB.AccountSummaryEventArgs e)
        {
            Log.Trace($"InteractiveBrokersBrokerage.HandleAccountSummary(): Request id: {e.RequestId}, Account: {e.Account}, Tag: {e.Tag}, Value: {e.Value}, Currency: {e.Currency}");
        }

        private void HandleFamilyCodes(object sender, IB.FamilyCodesEventArgs e)
        {
            foreach (var familyCode in e.FamilyCodes)
            {
                Log.Trace($"InteractiveBrokersBrokerage.HandleFamilyCodes(): Account id: {familyCode.AccountID}, Family code: {familyCode.FamilyCodeStr}");
            }
        }

        private void HandleManagedAccounts(object sender, IB.ManagedAccountsEventArgs e)
        {
            Log.Trace($"InteractiveBrokersBrokerage.HandleManagedAccounts(): Account list: {e.AccountList}");
        }

        private readonly ConcurrentDictionary<Symbol, int> _subscribedSymbols = new ConcurrentDictionary<Symbol, int>();
        private readonly ConcurrentDictionary<int, SubscriptionEntry> _subscribedTickers = new ConcurrentDictionary<int, SubscriptionEntry>();
        private readonly Dictionary<Symbol, Symbol> _underlyings = new Dictionary<Symbol, Symbol>();

        private class SubscriptionEntry
        {
            public Symbol Symbol { get; set; }
            public decimal PriceMagnifier { get; set; }
            public Tick LastTradeTick { get; set; }
            public Tick LastQuoteTick { get; set; }
            public Tick LastOpenInterestTick { get; set; }
        }

        private class ModulesReadLicenseRead : Api.RestResponse
        {
            [JsonProperty(PropertyName = "license")]
            public string License;
            [JsonProperty(PropertyName = "organizationId")]
            public string OrganizationId;
        }

        /// <summary>
        /// Validate the user of this project has permission to be using it via our web API.
        /// </summary>
        private static void ValidateSubscription()
        {
            try
            {
                var productId = 181;
                var userId = Config.GetInt("job-user-id");
                var token = Config.Get("api-access-token");
                var organizationId = Config.Get("job-organization-id", null);
                // Verify we can authenticate with this user and token
                var api = new ApiConnection(userId, token);
                if (!api.Connected)
                {
                    throw new ArgumentException("Invalid api user id or token, cannot authenticate subscription.");
                }
                // Compile the information we want to send when validating
                var information = new Dictionary<string, object>()
                {
                    {"productId", productId},
                    {"machineName", Environment.MachineName},
                    {"userName", Environment.UserName},
                    {"domainName", Environment.UserDomainName},
                    {"os", Environment.OSVersion}
                };
                // IP and Mac Address Information
                try
                {
                    var interfaceDictionary = new List<Dictionary<string, object>>();
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up))
                    {
                        var interfaceInformation = new Dictionary<string, object>();
                        // Get UnicastAddresses
                        var addresses = nic.GetIPProperties().UnicastAddresses
                            .Select(uniAddress => uniAddress.Address)
                            .Where(address => !IPAddress.IsLoopback(address)).Select(x => x.ToString());
                        // If this interface has non-loopback addresses, we will include it
                        if (!addresses.IsNullOrEmpty())
                        {
                            interfaceInformation.Add("unicastAddresses", addresses);
                            // Get MAC address
                            interfaceInformation.Add("MAC", nic.GetPhysicalAddress().ToString());
                            // Add Interface name
                            interfaceInformation.Add("name", nic.Name);
                            // Add these to our dictionary
                            interfaceDictionary.Add(interfaceInformation);
                        }
                    }
                    information.Add("networkInterfaces", interfaceDictionary);
                }
                catch (Exception)
                {
                    // NOP, not necessary to crash if fails to extract and add this information
                }
                // Include our OrganizationId is specified
                if (!string.IsNullOrEmpty(organizationId))
                {
                    information.Add("organizationId", organizationId);
                }
                var request = new RestRequest("modules/license/read", Method.POST) { RequestFormat = DataFormat.Json };
                request.AddParameter("application/json", JsonConvert.SerializeObject(information), ParameterType.RequestBody);
                api.TryRequest(request, out ModulesReadLicenseRead result);
                if (!result.Success)
                {
                    throw new InvalidOperationException($"Request for subscriptions from web failed, Response Errors : {string.Join(',', result.Errors)}");
                }

                var encryptedData = result.License;
                // Decrypt the data we received
                DateTime? expirationDate = null;
                long? stamp = null;
                bool? isValid = null;
                if (encryptedData != null)
                {
                    // Fetch the org id from the response if we are null, we need it to generate our validation key
                    if (string.IsNullOrEmpty(organizationId))
                    {
                        organizationId = result.OrganizationId;
                    }
                    // Create our combination key
                    var password = $"{token}-{organizationId}";
                    var key = SHA256.HashData(Encoding.UTF8.GetBytes(password));
                    // Split the data
                    var info = encryptedData.Split("::");
                    var buffer = Convert.FromBase64String(info[0]);
                    var iv = Convert.FromBase64String(info[1]);
                    // Decrypt our information
                    using var aes = new AesManaged();
                    var decryptor = aes.CreateDecryptor(key, iv);
                    using var memoryStream = new MemoryStream(buffer);
                    using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
                    using var streamReader = new StreamReader(cryptoStream);
                    var decryptedData = streamReader.ReadToEnd();
                    if (!decryptedData.IsNullOrEmpty())
                    {
                        var jsonInfo = JsonConvert.DeserializeObject<JObject>(decryptedData);
                        expirationDate = jsonInfo["expiration"]?.Value<DateTime>();
                        isValid = jsonInfo["isValid"]?.Value<bool>();
                        stamp = jsonInfo["stamped"]?.Value<int>();
                    }
                }
                // Validate our conditions
                if (!expirationDate.HasValue || !isValid.HasValue || !stamp.HasValue)
                {
                    throw new InvalidOperationException("Failed to validate subscription.");
                }

                var nowUtc = DateTime.UtcNow;
                var timeSpan = nowUtc - Time.UnixTimeStampToDateTime(stamp.Value);
                if (timeSpan > TimeSpan.FromHours(12))
                {
                    throw new InvalidOperationException("Invalid API response.");
                }
                if (!isValid.Value)
                {
                    throw new ArgumentException($"Your subscription is not valid, please check your product subscriptions on our website.");
                }
                if (expirationDate < nowUtc)
                {
                    throw new ArgumentException($"Your subscription expired {expirationDate}, please renew in order to use this product.");
                }
            }
            catch (Exception e)
            {
                Log.Error($"ValidateSubscription(): Failed during validation, shutting down. Error : {e.Message}");
                Environment.Exit(1);
            }
        }

        private static class AccountValueKeys
        {
            public const string CashBalance = "CashBalance";
            public const string ExchangeRate = "ExchangeRate";
        }

        // these are fatal errors from IB
        private static readonly HashSet<int> ErrorCodes = new HashSet<int>
        {
            100, 101, 103, 138, 139, 142, 143, 144, 145, 200, 203, 300,301,302,306,308,309,310,311,316,317,320,321,322,323,324,326,327,330,331,332,333,344,346,354,357,365,366,381,384,401,414,431,432,438,501,502,503,504,505,506,507,508,510,511,512,513,514,515,516,517,518,519,520,521,522,523,524,525,526,527,528,529,530,531,10000,10001,10005,10013,10015,10016,10021,10022,10023,10024,10025,10026,10027,1300
        };

        // these are warning messages from IB
        private static readonly HashSet<int> WarningCodes = new HashSet<int>
        {
            102, 104, 105, 106, 107, 109, 110, 111, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 129, 131, 132, 133, 134, 135, 136, 137, 140, 141, 146, 151, 152, 153, 154, 155, 156, 157, 158, 159, 160, 161, 162, 163, 164, 165, 166, 167, 168, 201, 303,313,314,315,319,325,328,329,334,335,336,337,338,339,340,341,342,343,345,347,348,349,350,352,353,355,356,358,359,360,361,362,363,364,367,368,369,370,371,372,373,374,375,376,377,378,379,380,382,383,385,386,387,388,389,390,391,392,393,394,395,396,397,398,399,400,402,403,404,405,406,407,408,409,410,411,412,413,417,418,419,420,421,422,423,424,425,426,427,428,429,430,433,434,435,436,437,439,440,441,442,443,444,445,446,447,448,449,450,10002,10003,10006,10007,10008,10009,10010,10011,10012,10014,10018,10019,10020,10052,10147,10148,10149,2100,2101,2102,2109,2148
        };

        // these require us to issue invalidated order events
        private static readonly HashSet<int> InvalidatingCodes = new HashSet<int>
        {
            105, 106, 107, 109, 110, 111, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 129, 131, 132, 133, 134, 135, 136, 137, 140, 141, 146, 147, 148, 151, 152, 153, 154, 155, 156, 157, 158, 159, 160, 161, 163, 167, 168, 201, 313,314,315,325,328,329,334,335,336,337,338,339,340,341,342,343,345,347,348,349,350,352,353,355,356,358,359,360,361,362,363,364,367,368,369,370,371,372,373,374,375,376,377,378,379,380,382,383,387,388,389,390,391,392,393,394,395,396,397,398,400,401,402,403,405,406,407,408,409,410,411,412,413,417,418,419,421,423,424,427,428,429,433,434,435,436,437,439,440,441,442,443,444,445,446,447,448,449,10002,10006,10007,10008,10009,10010,10011,10012,10014,10020,2102
        };

        // these are warning messages not sent as brokerage message events
        private static readonly HashSet<int> FilteredCodes = new HashSet<int>
        {
            1100, 1101, 1102, 2103, 2104, 2105, 2106, 2107, 2108, 2119, 2157, 2158, 10197
        };
    }
}
