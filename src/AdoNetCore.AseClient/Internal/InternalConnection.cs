﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using AdoNetCore.AseClient.Enum;
using AdoNetCore.AseClient.Interface;
using AdoNetCore.AseClient.Internal.Handler;
using AdoNetCore.AseClient.Packet;
using AdoNetCore.AseClient.Token;

namespace AdoNetCore.AseClient.Internal
{
    internal class InternalConnection : IInternalConnection
    {
        private readonly IConnectionParameters _parameters;
        private readonly ISocket _socket;
        private readonly DbEnvironment _environment = new DbEnvironment();

        private enum InternalConnectionState
        {
            None,
            Ready,
            Active,
            Canceled,
            Broken
        }

        private InternalConnectionState _state = InternalConnectionState.None;
        private readonly object _stateMutex = new object();

        private void SetState(InternalConnectionState newState)
        {
            lock (_stateMutex)
            {
                if (_state == InternalConnectionState.Broken)
                {
                    throw new ArgumentException("Cannot change internal connection state as it is Broken");
                }
                _state = newState;
            }
        }

        private bool TrySetState(InternalConnectionState newState, Func<InternalConnectionState, bool> predicate)
        {
            lock (_stateMutex)
            {
                if (_state == InternalConnectionState.Broken || !predicate(_state))
                {
                    return false;
                }

                _state = newState;
                return true;
            }
        }

        public InternalConnection(IConnectionParameters parameters, ISocket socket)
        {
            _parameters = parameters;
            _socket = socket;
            _environment.PacketSize = parameters.PacketSize; //server might decide to change the packet size later anyway
        }

        private void SendPacket(IPacket packet)
        {
            Logger.Instance?.WriteLine();
            Logger.Instance?.WriteLine("----------  Send packet   ----------");
            _socket.SendPacket(packet, _environment);
        }

        private void ReceiveTokens(params ITokenHandler[] handlers)
        {
            Logger.Instance?.WriteLine();
            Logger.Instance?.WriteLine("---------- Receive Tokens ----------");
            foreach (var receivedToken in _socket.ReceiveTokens(_environment))
            {
                foreach (var handler in handlers)
                {
                    if (handler.CanHandle(receivedToken.Type))
                    {
                        handler.Handle(receivedToken);
                    }
                }
            }
        }

        public void Login()
        {
            //socket is established already
            //login
            SendPacket(new LoginPacket(
                    _parameters.ClientHostName,
                    _parameters.Username,
                    _parameters.Password,
                    _parameters.ProcessId,
                    _parameters.ApplicationName,
                    _parameters.Server,
                    "us_english",
                    _parameters.Charset,
                    "ADO.NET",
                    _environment.PacketSize,
                    new CapabilityToken()));

            var ackHandler = new LoginTokenHandler();
            var messageHandler = new MessageTokenHandler();

            ReceiveTokens(
                ackHandler,
                new EnvChangeTokenHandler(_environment),
                messageHandler);

            messageHandler.AssertNoErrors();

            if (!ackHandler.ReceivedAck)
            {
                IsDoomed = true;
                throw new InvalidOperationException("No login ack found");
            }

            Created = DateTime.UtcNow;
            SetState(InternalConnectionState.Ready);
        }

        public DateTime Created { get; private set; }
        public DateTime LastActive => _socket.LastActive;

        public bool Ping()
        {
            try
            {
                AssertExecutionStart();
                SendPacket(new NormalPacket(OptionCommandToken.CreateGet(OptionCommandToken.OptionType.TDS_OPT_STAT_TIME)));

                var messageHandler = new MessageTokenHandler();

                ReceiveTokens(messageHandler);

                AssertExecutionCompletion();
                messageHandler.AssertNoErrors();

                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance?.WriteLine($"Internal ping resulted in exception: {ex}");
                IsDoomed = true;
                return false;
            }
        }

        public void ChangeDatabase(string databaseName)
        {
            if (string.IsNullOrWhiteSpace(databaseName) || string.Equals(databaseName, Database, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AssertExecutionStart();

            //turns out, you can't issue an env change token to change the database, it responds saying it doesn't know how to process such a token
            SendPacket(new NormalPacket(new LanguageToken
            {
                HasParameters = false,
                CommandText = $"USE {databaseName}"
            }));

            var messageHandler = new MessageTokenHandler();

            ReceiveTokens(
                new EnvChangeTokenHandler(_environment),
                messageHandler);

            AssertExecutionCompletion();

            messageHandler.AssertNoErrors();
        }

        public string Database => _environment.Database;

        public int ExecuteNonQuery(AseCommand command, AseTransaction transaction)
        {
            AssertExecutionStart();

            SendPacket(new NormalPacket(BuildCommandTokens(command)));

            var doneHandler = new DoneTokenHandler();
            var messageHandler = new MessageTokenHandler();

            ReceiveTokens(
                new EnvChangeTokenHandler(_environment),
                messageHandler,
                new ResponseParameterTokenHandler(command.AseParameters),
                doneHandler);

            AssertExecutionCompletion(doneHandler);

            if (transaction != null && doneHandler.TransactionState == TranState.TDS_TRAN_ABORT)
            {
                transaction.MarkAborted();
            }

            messageHandler.AssertNoErrors();

            return doneHandler.RowsAffected;
        }

        //todo: might be able to change this so that TCS is passed in as parameter, and clean up the AseCommand implementation
        public Task<int> ExecuteNonQueryAsTask(AseCommand command, AseTransaction transaction)
        {
            AssertExecutionStart();

            var source = new TaskCompletionSource<int>();

            try
            {
                SendPacket(new NormalPacket(BuildCommandTokens(command)));

                var doneHandler = new DoneTokenHandler();
                var messageHandler = new MessageTokenHandler();

                ReceiveTokens(
                    new EnvChangeTokenHandler(_environment),
                    messageHandler,
                    new ResponseParameterTokenHandler(command.AseParameters),
                    doneHandler);

                AssertExecutionCompletion(doneHandler);

                if (transaction != null && doneHandler.TransactionState == TranState.TDS_TRAN_ABORT)
                {
                    transaction.MarkAborted();
                }

                messageHandler.AssertNoErrors();

                if (doneHandler.Canceled)
                {

                    source.SetCanceled();
                }
                else
                {
                    source.SetResult(doneHandler.RowsAffected);
                }
            }
            catch (Exception ex)
            {
                source.SetException(ex);
            }

            return source.Task;
        }

        public AseDataReader ExecuteReader(CommandBehavior behavior, AseCommand command, AseTransaction transaction)
        {
            AssertExecutionStart();

            SendPacket(new NormalPacket(BuildCommandTokens(command)));

            var doneHandler = new DoneTokenHandler();
            var messageHandler = new MessageTokenHandler();
            var dataReaderHandler = new DataReaderTokenHandler();

            ReceiveTokens(
                new EnvChangeTokenHandler(_environment),
                messageHandler,
                dataReaderHandler,
                new ResponseParameterTokenHandler(command.AseParameters),
                doneHandler);

            if (transaction != null && doneHandler.TransactionState == TranState.TDS_TRAN_ABORT)
            {
                transaction.MarkAborted();
            }

            if (doneHandler.Canceled)
            {
                TrySetState(InternalConnectionState.Ready, s => s == InternalConnectionState.Canceled);
            }

            AssertExecutionCompletion(doneHandler);

            messageHandler.AssertNoErrors();

            return new AseDataReader(dataReaderHandler.Results());
        }

        public object ExecuteScalar(AseCommand command, AseTransaction transaction)
        {
            using (var reader = (IDataReader)ExecuteReader(CommandBehavior.Default, command, transaction))
            {
                if (reader.Read())
                {
                    return reader[0];
                }
            }

            return null;
        }

        public void Cancel()
        {
            if (TrySetState(InternalConnectionState.Canceled, s => s == InternalConnectionState.Active))
            {
                Logger.Instance?.WriteLine("Canceling...");
                SendPacket(new AttentionPacket());
            }
            else
            {
                Logger.Instance?.WriteLine("");
            }
        }

        private void AssertExecutionStart()
        {
            if(!TrySetState(InternalConnectionState.Active, s => s == InternalConnectionState.Ready))
            {
                IsDoomed = true;
                throw new AseException("Connection entered broken state");
            }
        }

        private void AssertExecutionCompletion(DoneTokenHandler doneHandler = null)
        {
            if (doneHandler?.Canceled == true)
            {
                TrySetState(InternalConnectionState.Ready, s => s == InternalConnectionState.Canceled);
            }

            if (_state == InternalConnectionState.Canceled)
            {
                //we're in a broken state
                IsDoomed = true;
                throw new AseException("Connection entered broken state");
            }

            TrySetState(InternalConnectionState.Ready, s => s == InternalConnectionState.Active);
        }

        public void GetTextSize()
        {
            SendPacket(new NormalPacket(OptionCommandToken.CreateGet(OptionCommandToken.OptionType.TDS_OPT_TEXTSIZE)));

            var doneHandler = new DoneTokenHandler();
            var messageHandler = new MessageTokenHandler();
            var dataReaderHandler = new DataReaderTokenHandler();

            ReceiveTokens(
                new EnvChangeTokenHandler(_environment),
                messageHandler,
                dataReaderHandler,
                doneHandler);

            messageHandler.AssertNoErrors();
        }

        public void SetTextSize(int textSize)
        {
            //todo: may need to remove this, user scripts could change the textsize value
            if (_environment.TextSize == textSize)
            {
                return;
            }

            SendPacket(new NormalPacket(OptionCommandToken.CreateSetTextSize(textSize)));

            var doneHandler = new DoneTokenHandler();
            var messageHandler = new MessageTokenHandler();
            var dataReaderHandler = new DataReaderTokenHandler();

            ReceiveTokens(
                new EnvChangeTokenHandler(_environment),
                messageHandler,
                dataReaderHandler,
                doneHandler);

            messageHandler.AssertNoErrors();

            _environment.TextSize = textSize;
        }

        private bool _isDoomed;
        public bool IsDoomed
        {
            get => _isDoomed;
            set
            {
                if (value)
                {
                    SetState(InternalConnectionState.Broken);
                }
                _isDoomed = _isDoomed || value;
            }
        }

        public bool IsDisposed { get; private set; }

        private IEnumerable<IToken> BuildCommandTokens(AseCommand command)
        {
            if (command.CommandType == CommandType.TableDirect)
            {
                throw new NotImplementedException($"{command.CommandType} is not implemented");
            }

            yield return command.CommandType == CommandType.StoredProcedure
                ? BuildRpcToken(command)
                : BuildLanguageToken(command);

            foreach (var token in BuildParameterTokens(command.AseParameters))
            {
                yield return token;
            }
        }

        private IToken BuildLanguageToken(AseCommand command)
        {
            return new LanguageToken
            {
                CommandText = command.CommandText,
                HasParameters = command.HasSendableParameters
            };
        }

        private IToken BuildRpcToken(AseCommand command)
        {
            return new DbRpcToken
            {
                ProcedureName = command.CommandText,
                HasParameters = command.HasSendableParameters
            };
        }

        private IToken[] BuildParameterTokens(AseParameterCollection parameters)
        {
            var formatItems = new List<FormatItem>();
            var parameterItems = new List<ParametersToken.Parameter>();

            foreach (var parameter in parameters.SendableParameters)
            {
                var parameterType = (DbType)parameter.AseDbType;
                var length = TypeMap.GetFormatLength(parameterType, parameter, _environment.Encoding);
                var formatItem = new FormatItem
                {
                    ParameterName = parameter.ParameterName,
                    DataType = TypeMap.GetTdsDataType(parameterType, parameter.Value, length),
                    IsOutput = parameter.IsOutput,
                    IsNullable = parameter.IsNullable,
                    Length = length
                };

                if ((parameterType == DbType.Decimal
                    || parameterType == DbType.VarNumeric
                    || parameterType == DbType.Currency
                ) && parameter.Value is decimal)
                {
                    var sqlDecimal = (SqlDecimal)(decimal)parameter.Value;
                    formatItem.Precision = sqlDecimal.Precision;
                    formatItem.Scale = sqlDecimal.Scale;
                }

                if (parameterType == DbType.String)
                {
                    formatItem.UserType = 35;
                }

                if (parameterType == DbType.StringFixedLength)
                {
                    formatItem.UserType = 34;
                }

                formatItems.Add(formatItem);
                parameterItems.Add(new ParametersToken.Parameter
                {
                    Format = formatItem,
                    Value = parameter.Value
                });
            }

            if (formatItems.Count == 0)
            {
                return new IToken[0];
            }

            return new IToken[]
            {
                new ParameterFormat2Token
                {
                    Formats = formatItems.ToArray()
                },
                new ParametersToken
                {
                    Parameters = parameterItems.ToArray()
                }
            };
        }

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            _socket.Dispose();
        }
    }
}
