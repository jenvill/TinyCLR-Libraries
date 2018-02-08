﻿using System;
using System.Collections;
using System.Net;
using System.Net.NetworkInterface;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.Spi;

namespace GHIElectronics.TinyCLR.Networking.SPWF04Sx {
    public delegate object PoolObjectCreator();

    public class Pool {
        private readonly ArrayList all = new ArrayList();
        private readonly Stack available = new Stack();
        private readonly PoolObjectCreator creator;

        public Pool(PoolObjectCreator creator) => this.creator = creator;

        public virtual object Acquire() {
            lock (this.available) {
                if (this.available.Count == 0) {
                    var obj = this.creator();

                    this.all.Add(obj);

                    return obj;
                }

                return this.available.Pop();
            }
        }

        public virtual void Release(object obj) {
            if (!this.all.Contains(obj)) throw new ArgumentException();

            lock (this.available)
                this.available.Push(obj);
        }

        public virtual void ResetAll() {
            this.available.Clear();

            foreach (var obj in this.all)
                this.available.Push(obj);
        }
    }

    public class Buffer {
        public byte[] Data;

        public int AvailableWrite { get { lock (this) return this.Data.Length - this.nextWrite; } }
        public int AvailableRead { get { lock (this) return this.nextWrite - this.nextRead; } }

        public int WriteOffset => this.nextWrite;
        public int ReadOffset => this.nextRead;

        private int nextRead;
        private int nextWrite;

        public Buffer(int size) => this.Data = new byte[size];

        public void TryCompress() {
            lock (this) {
                if (this.nextRead != 0) {
                    Array.Copy(this.Data, this.nextRead, this.Data, 0, this.nextWrite - this.nextRead);

                    this.nextWrite -= this.nextRead;
                    this.nextRead = 0;
                }
            }
        }

        public void Write(int count) {
            lock (this)
                this.nextWrite += count;
        }

        public void Read(int count) {
            lock (this)
                this.nextRead += count;
        }

        public void Reset() {
            this.nextWrite = 0;
            this.nextRead = 0;
        }
    }

    public class OperationPool : Pool {
        private readonly Pool buffers = new Pool(() => new Buffer(1500 + 512));

        public OperationPool() : base(() => new Operation()) { }

        public new Operation Acquire() {
            var op = (Operation)base.Acquire();
            op.Buffer = (Buffer)this.buffers.Acquire();
            return op;
        }

        public override void Release(object obj) {
            var op = (Operation)obj;
            op.Buffer.Reset();
            this.buffers.Release(op.Buffer);
            op.Reset();
            base.Release(op);
        }

        public override void ResetAll() {
            this.buffers.ResetAll();
            base.ResetAll();
        }
    }

    //TODO Switch to semaphore/mutex/whatever instead of spin waiting, possibly timeout too.
    public class Operation {
        public string[] Parameters = new string[16];
        public int ParamentCount;
        public byte[] WriteHeader = new byte[4];
        public int WriteHeaderLength;
        public byte[] WritePayload;
        public int WritePayloadOffset;
        public int WritePayloadLength;

        public bool Written;

        public Queue PendingReads = new Queue();

        //TODO Since requests can only ever be done sequentially, assign this buffer when making an op active. Can probably reuse the WIND buffer. See about customizing the size and its effects.
        public Buffer Buffer;

        private int partialRead;

        public string ReadString() {
            while (!this.Written)
                Thread.Sleep(1);

            while (!this.DataAvailable)
                Thread.Sleep(1);

            lock (this.PendingReads) {
                var start = this.Buffer.ReadOffset;
                var len = (int)this.PendingReads.Dequeue();
                var res = Encoding.UTF8.GetString(this.Buffer.Data, start, len);

                this.Buffer.Read(len);

                return res;
            }
        }

        public int ReadBuffer() => this.ReadBuffer(null, 0, 0);

        public int ReadBuffer(byte[] buffer, int offset, int count) {
            while (!this.Written)
                Thread.Sleep(1);

            while (!this.DataAvailable)
                Thread.Sleep(1);

            lock (this.PendingReads) {
                var len = 0;

                if (buffer != null) {
                    len = (int)this.PendingReads.Peek() - this.partialRead;

                    if (len <= count) {
                        Array.Copy(this.Buffer.Data, this.Buffer.ReadOffset, buffer, offset, len);

                        this.partialRead = 0;

                        this.PendingReads.Dequeue();
                    }
                    else {
                        len = count;

                        Array.Copy(this.Buffer.Data, this.Buffer.ReadOffset, buffer, offset, count);

                        this.partialRead += count;
                    }
                }
                else {
                    len = (int)this.PendingReads.Dequeue();
                }

                this.Buffer.Read(len);

                return len;
            }
        }

        public int Peek() {
            while (!this.DataAvailable)
                Thread.Sleep(10);

            return (int)this.PendingReads.Peek();
        }

        public bool DataAvailable {
            get {
                lock (this.PendingReads)
                    return this.PendingReads.Count != 0;
            }
        }

        public void MarkWritten(int count) {
            lock (this.PendingReads) {
                this.Buffer.Write(count);
                this.PendingReads.Enqueue(count);
            }
        }

        public Operation AddParameter(string parameter) {
            this.Parameters[this.ParamentCount++] = parameter;
            return this;
        }

        public void Reset() {
            this.Written = false;
            this.ParamentCount = 0;
            this.WriteHeaderLength = 0;
            this.WritePayloadOffset = 0;
            this.WritePayloadLength = 0;

            this.PendingReads.Clear();

            this.Buffer = null;
        }

        public Operation SetCommand(SPWF04SxCommandIds cmdId) => this.SetCommand(cmdId, null, 0, 0);

        public Operation SetCommand(SPWF04SxCommandIds cmdId, byte[] rawData, int rawDataOffset, int rawDataCount) {
            if (rawData == null && rawDataCount != 0) throw new ArgumentException();
            if (rawDataOffset < 0) throw new ArgumentOutOfRangeException();
            if (rawDataCount < 0) throw new ArgumentOutOfRangeException();
            if (rawData != null && rawDataOffset + rawDataCount > rawData.Length) throw new ArgumentOutOfRangeException();

            this.EnsureWriteHeaderSize();

            var idx = 0;

            this.WriteHeader[idx++] = 0x00;
            this.WriteHeader[idx++] = 0x00;

            this.WriteHeader[idx++] = (byte)cmdId;
            this.WriteHeader[idx++] = (byte)this.ParamentCount;

            for (var i = 0; i < this.ParamentCount; i++) {
                var p = this.Parameters[i];
                var pLen = p != null ? p.Length : 0;

                this.WriteHeader[idx++] = (byte)pLen;

                if (!string.IsNullOrEmpty(p))
                    Encoding.UTF8.GetBytes(p, 0, pLen, this.WriteHeader, idx);

                idx += pLen;
            }

            var len = idx + rawDataCount - 2;
            this.WriteHeader[0] = (byte)((len >> 8) & 0xFF);
            this.WriteHeader[1] = (byte)((len >> 0) & 0xFF);

            this.WritePayload = rawData;
            this.WritePayloadOffset = rawDataOffset;
            this.WritePayloadLength = rawDataCount;
            this.WriteHeaderLength = idx;

            return this;
        }

        private void EnsureWriteHeaderSize() {
            var required = 4;

            for (var i = 0; i < this.ParamentCount; i++) {
                var p = this.Parameters[i];

                required += p != null ? p.Length : 0;
            }

            if (required > this.WriteHeader.Length)
                this.WriteHeader = new byte[(int)Math.Pow(2, Math.Ceiling(Math.Log(required) / 0.69314718055994529))]; //~0.69 = ln(2), for change of base from e to 2.
        }
    }

    public class SPWF04SxInterface : NetworkInterface, ISocket, IDns, IDisposable {
        private readonly OperationPool operationPool;
        private readonly Hashtable netifSockets;
        private readonly Queue pendingOperations;
        private readonly byte[] readHeaderBuffer;
        private readonly byte[] windPayloadBuffer;
        private readonly byte[] syncRead;
        private readonly byte[] syncWrite;
        private readonly SpiDevice spi;
        private readonly GpioPin irq;
        private readonly GpioPin reset;
        private Operation activeOperation;
        private Operation activeHttpOperation;
        private Thread worker;
        private bool running;
        private int nextSocketId;

        public event SPWF04SxIndicationReceivedEventHandler IndicationReceived;
        public event SPWF04SxErrorReceivedEventHandler ErrorReceived;

        public SPWF04SxWiFiState State { get; private set; }
        public bool ForceSocketsTls { get; set; }
        public string ForceSocketsTlsCommonName { get; set; }

        public static SpiConnectionSettings GetConnectionSettings(int chipSelectLine) => new SpiConnectionSettings(chipSelectLine) {
            ClockFrequency = 4000000,
            Mode = SpiMode.Mode0,
            SharingMode = SpiSharingMode.Exclusive,
            DataBitLength = 8
        };

        public SPWF04SxInterface(SpiDevice spi, GpioPin irq, GpioPin reset) {
            this.operationPool = new OperationPool();
            this.netifSockets = new Hashtable();
            this.pendingOperations = new Queue();
            this.readHeaderBuffer = new byte[4];
            this.windPayloadBuffer = new byte[1500 + 512]; //Longest payload, set by the socket heap variable, plus overhead for other result codes and WINDs
            this.syncRead = new byte[1];
            this.syncWrite = new byte[1];
            this.spi = spi;
            this.irq = irq;
            this.reset = reset;

            this.State = SPWF04SxWiFiState.RadioTerminatedByUser;

            this.reset.SetDriveMode(GpioPinDriveMode.Output);
            this.reset.Write(GpioPinValue.Low);

            this.irq.SetDriveMode(GpioPinDriveMode.Input);

            NetworkInterface.RegisterNetworkInterface(this);
        }

        ~SPWF04SxInterface() => this.Dispose(false);

        public void Dispose() {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (disposing) {
                this.TurnOff();

                this.spi.Dispose();
                this.irq.Dispose();
                this.reset.Dispose();

                NetworkInterface.DeregisterNetworkInterface(this);
            }
        }

        public void TurnOn() {
            if (this.running) return;

            this.running = true;
            this.worker = new Thread(this.Process);
            this.worker.Start();

            this.reset.SetDriveMode(GpioPinDriveMode.Input);
        }

        public void TurnOff() {
            if (!this.running) return;

            this.reset.SetDriveMode(GpioPinDriveMode.Output);
            this.reset.Write(GpioPinValue.Low);

            this.running = false;
            this.worker.Join();
            this.worker = null;

            this.pendingOperations.Clear();

            this.netifSockets.Clear();
            this.nextSocketId = 0;
            this.activeOperation = null;
            this.activeHttpOperation = null;

            this.operationPool.ResetAll();
        }

        protected Operation GetOperation() => this.operationPool.Acquire();

        protected void EnqueueOperation(Operation op) {
            lock (this.pendingOperations) {
                if (this.activeOperation != null) {
                    this.pendingOperations.Enqueue(op);
                }
                else {
                    this.activeOperation = op;
                }
            }
        }

        protected void FinishOperation(Operation op) {
            if (this.activeOperation != op || !this.activeOperation.Written) throw new ArgumentException();

            lock (this.pendingOperations) {
                this.operationPool.Release(op);

                this.activeOperation = this.pendingOperations.Count != 0 ? (Operation)this.pendingOperations.Dequeue() : null;
            }
        }

        public void ClearTlsServerRootCertificate() {
            var op = this.GetOperation()
                .AddParameter("content")
                .AddParameter("2")
                .SetCommand(SPWF04SxCommandIds.TLSCERT);

            this.EnqueueOperation(op);

            op.ReadBuffer();
            op.ReadBuffer();
            this.FinishOperation(op);
        }

        public string SetTlsServerRootCertificate(byte[] certificate) {
            if (certificate == null) throw new ArgumentNullException();

            var op = this.GetOperation()
                .AddParameter("ca")
                .AddParameter(certificate.Length.ToString())
                .SetCommand(SPWF04SxCommandIds.TLSCERT, certificate, 0, certificate.Length);

            this.EnqueueOperation(op);

            var result = op.ReadString();

            op.ReadBuffer();

            this.FinishOperation(op);

            return result.Substring(result.IndexOf(':') + 1);
        }

        public int SendHttpGet(string host, string path, int port, SPWF04SxConnectionSecurityType connectionSecurity) {
            if (this.activeHttpOperation != null) throw new InvalidOperationException();

            this.activeHttpOperation = this.GetOperation()
                .AddParameter(host)
                .AddParameter(path)
                .AddParameter(port.ToString())
                .AddParameter(connectionSecurity == SPWF04SxConnectionSecurityType.None ? "0" : "2")
                .AddParameter(null)
                .AddParameter(null)
                .AddParameter(null)
                .AddParameter(null)
                .SetCommand(SPWF04SxCommandIds.HTTPGET);

            this.EnqueueOperation(this.activeHttpOperation);

            var result = this.activeHttpOperation.ReadString();
            if (connectionSecurity == SPWF04SxConnectionSecurityType.Tls && result == string.Empty) {
                result = this.activeHttpOperation.ReadString();

                if (result.IndexOf("Loading:") == 0)
                    result = this.activeHttpOperation.ReadString();
            }

            return result.Split(':') is var parts && parts[0] == "Http Server Status Code" ? int.Parse(parts[1]) : throw new Exception($"Request failed: {result}");
        }

        //TODO Need to test on an actual server
        public int SendHttpPost(string host, string path, int port, SPWF04SxConnectionSecurityType connectionSecurity) {
            if (this.activeHttpOperation != null) throw new InvalidOperationException();

            this.activeHttpOperation = this.GetOperation()
                .AddParameter(host)
                .AddParameter(path)
                .AddParameter(port.ToString())
                .AddParameter(connectionSecurity == SPWF04SxConnectionSecurityType.None ? "0" : "2")
                .AddParameter(null)
                .AddParameter(null)
                .AddParameter(null)
                .AddParameter(null)
                .SetCommand(SPWF04SxCommandIds.HTTPPOST);

            this.EnqueueOperation(this.activeHttpOperation);

            var result = this.activeHttpOperation.ReadString();
            if (connectionSecurity == SPWF04SxConnectionSecurityType.Tls && result == string.Empty) {
                result = this.activeHttpOperation.ReadString();

                if (result.IndexOf("Loading:") == 0)
                    result = this.activeHttpOperation.ReadString();
            }

            return result.Split(':') is var parts && parts[0] == "Http Server Status Code" ? int.Parse(parts[1]) : throw new Exception($"Request failed: {result}");
        }

        public int ReadHttpResponse(byte[] buffer, int offset, int count) {
            if (this.activeHttpOperation == null) throw new InvalidOperationException();

            while (!this.activeHttpOperation.DataAvailable)
                Thread.Sleep(1);

            var len = this.activeHttpOperation.ReadBuffer(buffer, offset, count);

            if (len == 0) {
                this.FinishOperation(this.activeHttpOperation);

                this.activeHttpOperation = null;
            }

            return len;
        }

        public int OpenSocket(string host, int port, SPWF04SxConnectionyType connectionType, SPWF04SxConnectionSecurityType connectionSecurity, string commonName = null) {
            var op = this.GetOperation()
                .AddParameter(host)
                .AddParameter(port.ToString())
                .AddParameter(null)
                .AddParameter(commonName ?? (connectionType == SPWF04SxConnectionyType.Tcp ? (connectionSecurity == SPWF04SxConnectionSecurityType.Tls ? "s" : "t") : "u"))
                .SetCommand(SPWF04SxCommandIds.SOCKON);

            this.EnqueueOperation(op);

            var a = op.ReadString();
            var b = op.ReadString();

            if (connectionSecurity == SPWF04SxConnectionSecurityType.Tls && b.IndexOf("Loading:") == 0) {
                a = op.ReadString();
                b = op.ReadString();
            }

            this.FinishOperation(op);

            return a.Split(':') is var result && result[0] == "On" ? int.Parse(result[2]) : throw new Exception("Request failed");
        }

        public void CloseSocket(int socket) {
            var op = this.GetOperation()
                .AddParameter(socket.ToString())
                .SetCommand(SPWF04SxCommandIds.SOCKC);

            this.EnqueueOperation(op);

            op.ReadBuffer();

            this.FinishOperation(op);
        }

        public void WriteSocket(int socket, byte[] data) => this.WriteSocket(socket, data, 0, data != null ? data.Length : throw new ArgumentNullException(nameof(data)));

        public void WriteSocket(int socket, byte[] data, int offset, int count) {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0) throw new ArgumentOutOfRangeException();
            if (count < 0) throw new ArgumentOutOfRangeException();
            if (offset + count > data.Length) throw new ArgumentOutOfRangeException();

            var op = this.GetOperation()
                .AddParameter(socket.ToString())
                .AddParameter(count.ToString())
                .SetCommand(SPWF04SxCommandIds.SOCKW, data, offset, count);

            this.EnqueueOperation(op);

            op.ReadBuffer();

            this.FinishOperation(op);
        }

        public int ReadSocket(int socket, byte[] buffer, int offset, int count) {
            var op = this.GetOperation()
                .AddParameter(socket.ToString())
                .AddParameter(count.ToString())
                .SetCommand(SPWF04SxCommandIds.SOCKR);

            this.EnqueueOperation(op);

            op.ReadBuffer();

            var current = 0;
            var total = 0;
            do {
                current = op.ReadBuffer(buffer, offset + total, count - total);
                total += current;
            } while (current != 0);

            this.FinishOperation(op);

            return total;
        }

        public int QuerySocket(int socket) {
            var op = this.GetOperation()
                .AddParameter(socket.ToString())
                .SetCommand(SPWF04SxCommandIds.SOCKQ);

            this.EnqueueOperation(op);

            var result = op.ReadString().Split(':');

            op.ReadBuffer();

            this.FinishOperation(op);

            return result[0] == "Query" ? int.Parse(result[1]) : throw new Exception("Request failed");
        }

        public string ListSocket() {
            var op = this.GetOperation()
                .SetCommand(SPWF04SxCommandIds.SOCKL);

            this.EnqueueOperation(op);

            var str = string.Empty;
            while (op.Peek() != 0)
                str += op.ReadString() + Environment.NewLine;

            op.ReadBuffer();

            this.FinishOperation(op);

            return str;
        }

        public void EnableRadio() {
            var op = this.GetOperation()
                .AddParameter("1")
                .SetCommand(SPWF04SxCommandIds.WIFI);

            this.EnqueueOperation(op);

            op.ReadBuffer();

            this.FinishOperation(op);
        }

        public void DisableRadio() {
            var op = this.GetOperation()
                .AddParameter("0")
                .SetCommand(SPWF04SxCommandIds.WIFI);

            this.EnqueueOperation(op);

            op.ReadBuffer();

            this.FinishOperation(op);
        }

        public void JoinNetwork(string ssid, string password) {
            this.DisableRadio();

            var op = this.GetOperation()
                .AddParameter("wifi_mode")
                .AddParameter("1")
                .SetCommand(SPWF04SxCommandIds.SCFG);
            this.EnqueueOperation(op);
            op.ReadBuffer();
            this.FinishOperation(op);

            op = this.GetOperation()
                .AddParameter("wifi_priv_mode")
                .AddParameter("2")
                .SetCommand(SPWF04SxCommandIds.SCFG);
            this.EnqueueOperation(op);
            op.ReadBuffer();
            this.FinishOperation(op);

            op = this.GetOperation()
                .AddParameter("wifi_wpa_psk_text")
                .AddParameter(password)
                .SetCommand(SPWF04SxCommandIds.SCFG);
            this.EnqueueOperation(op);
            op.ReadBuffer();
            this.FinishOperation(op);

            op = this.GetOperation()
                .AddParameter(ssid)
                .SetCommand(SPWF04SxCommandIds.SSIDTXT);
            this.EnqueueOperation(op);
            op.ReadBuffer();
            this.FinishOperation(op);

            this.EnableRadio();

            op = this.GetOperation()
                .SetCommand(SPWF04SxCommandIds.WCFG);
            this.EnqueueOperation(op);
            op.ReadBuffer();
            this.FinishOperation(op);
        }

        private void Process() {
            var pendingEvents = new Queue();

            while (this.running) {
                var hasWrite = this.activeOperation != null && !this.activeOperation.Written;
                var hasIrq = this.irq.Read() == GpioPinValue.Low;

                if (hasIrq || hasWrite) {
                    this.syncWrite[0] = (byte)(!hasIrq && hasWrite ? 0x02 : 0x00);

                    this.spi.TransferFullDuplex(this.syncWrite, this.syncRead);

                    if (!hasIrq && hasWrite && this.syncRead[0] != 0x02) {
                        this.spi.Write(this.activeOperation.WriteHeader, 0, this.activeOperation.WriteHeaderLength);

                        if (this.activeOperation.WritePayloadLength > 0) {
                            while (this.irq.Read() == GpioPinValue.High)
                                Thread.Sleep(0);

                            this.spi.Write(this.activeOperation.WritePayload, this.activeOperation.WritePayloadOffset, this.activeOperation.WritePayloadLength);

                            while (this.irq.Read() == GpioPinValue.Low)
                                Thread.Sleep(0);
                        }

                        this.activeOperation.Written = true;
                    }
                    else if (this.syncRead[0] == 0x02) {
                        this.spi.Read(this.readHeaderBuffer);

                        var status = this.readHeaderBuffer[0];
                        var ind = this.readHeaderBuffer[1];
                        var payloadLength = (this.readHeaderBuffer[3] << 8) | this.readHeaderBuffer[2];
                        var type = (status & 0b1111_0000) >> 4;

                        this.State = (SPWF04SxWiFiState)(status & 0b0000_1111);

                        if (type == 0x01 || type == 0x02) {
                            if (payloadLength > this.windPayloadBuffer.Length)
                                throw new InvalidOperationException("Unexpected WIND size.");

                            if (payloadLength > 0)
                                this.spi.Read(this.windPayloadBuffer, 0, payloadLength);

                            var str = Encoding.UTF8.GetString(this.windPayloadBuffer, 0, payloadLength);

                            pendingEvents.Enqueue(type == 0x01 ? new SPWF04SxIndicationReceivedEventArgs((SPWF04SxIndication)ind, str) : (object)new SPWF04SxErrorReceivedEventArgs(ind, str));
                        }
                        else {
                            if (this.activeOperation == null || !this.activeOperation.Written) throw new InvalidOperationException("Unexpected payload.");

                            if (payloadLength > 0) {
                                while (payloadLength > 0) {
                                    while (this.activeOperation.Buffer.AvailableWrite == 0) {
                                        this.activeOperation.Buffer.TryCompress();

                                        Thread.Sleep(20);
                                    }

                                    var min = Math.Min(payloadLength, this.activeOperation.Buffer.AvailableWrite);

                                    this.spi.Read(this.activeOperation.Buffer.Data, this.activeOperation.Buffer.WriteOffset, min);

                                    payloadLength -= min;

                                    this.activeOperation.MarkWritten(min);
                                }
                            }
                            else {
                                this.activeOperation.MarkWritten(0);
                            }
                        }
                    }
                }
                else {
                    while (pendingEvents.Count != 0) {
                        switch (pendingEvents.Dequeue()) {
                            case SPWF04SxIndicationReceivedEventArgs e: this.IndicationReceived?.Invoke(this, e); break;
                            case SPWF04SxErrorReceivedEventArgs e: this.ErrorReceived?.Invoke(this, e); break;
                        }
                    }
                }

                Thread.Sleep(1);
            }
        }

        private int GetInternalSocketId(int socket) => this.netifSockets.Contains(socket) ? (int)this.netifSockets[socket] : throw new ArgumentException();

        private void GetAddress(SocketAddress address, out string host, out int port) {
            port = 0;
            port |= address[2] << 8;
            port |= address[3] << 0;

            host = "";
            host += address[4] + ".";
            host += address[5] + ".";
            host += address[6] + ".";
            host += address[7];
        }

        int ISocket.Create(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType) {
            if (addressFamily != AddressFamily.InterNetwork || socketType != SocketType.Stream || protocolType != ProtocolType.Tcp) throw new ArgumentException();

            var id = this.nextSocketId++;

            this.netifSockets.Add(id, 0);

            return id;
        }

        int ISocket.Available(int socket) => this.QuerySocket(this.GetInternalSocketId(socket));

        void ISocket.Close(int socket) {
            this.CloseSocket(this.GetInternalSocketId(socket));

            this.netifSockets.Remove(socket);
        }

        void ISocket.Connect(int socket, SocketAddress address) {
            if (!this.netifSockets.Contains(socket)) throw new ArgumentException();
            if (address.Family != AddressFamily.InterNetwork) throw new ArgumentException();

            this.GetAddress(address, out var host, out var port);

            this.netifSockets[socket] = this.OpenSocket(host, port, SPWF04SxConnectionyType.Tcp, this.ForceSocketsTls ? SPWF04SxConnectionSecurityType.Tls : SPWF04SxConnectionSecurityType.None, this.ForceSocketsTls ? this.ForceSocketsTlsCommonName : null);
        }

        int ISocket.Send(int socket, byte[] buffer, int offset, int count, SocketFlags flags, int timeout) {
            if (flags != SocketFlags.None) throw new ArgumentException();

            this.WriteSocket(this.GetInternalSocketId(socket), buffer, offset, count);

            return count;
        }

        int ISocket.Receive(int socket, byte[] buffer, int offset, int count, SocketFlags flags, int timeout) {
            if (flags != SocketFlags.None) throw new ArgumentException();
            if (timeout != Timeout.Infinite && timeout < 0) throw new ArgumentException();

            var end = (timeout != Timeout.Infinite ? DateTime.UtcNow.AddMilliseconds(timeout) : DateTime.MaxValue).Ticks;
            var sock = this.GetInternalSocketId(socket);
            var avail = 0;

            do {
                avail = this.QuerySocket(sock);

                Thread.Sleep(1);
            } while (avail == 0 && DateTime.UtcNow.Ticks < end);

            return avail > 0 ? this.ReadSocket(sock, buffer, offset, Math.Min(avail, count)) : 0;
        }

        bool ISocket.Poll(int socket, int microSeconds, SelectMode mode) {
            switch (mode) {
                default: throw new ArgumentException();
                case SelectMode.SelectError: return false;
                case SelectMode.SelectWrite: return true;
                case SelectMode.SelectRead: return this.QuerySocket(this.GetInternalSocketId(socket)) != 0;
            }
        }

        void ISocket.Bind(int socket, SocketAddress address) => throw new NotImplementedException();
        void ISocket.Listen(int socket, int backlog) => throw new NotImplementedException();
        int ISocket.Accept(int socket) => throw new NotImplementedException();
        int ISocket.SendTo(int socket, byte[] buffer, int offset, int count, SocketFlags flags, int timeout, SocketAddress address) => throw new NotImplementedException();
        int ISocket.ReceiveFrom(int socket, byte[] buffer, int offset, int count, SocketFlags flags, int timeout, ref SocketAddress address) => throw new NotImplementedException();

        void ISocket.GetRemoteAddress(int socket, out SocketAddress address) => address = new SocketAddress(AddressFamily.InterNetwork, 16);
        void ISocket.GetLocalAddress(int socket, out SocketAddress address) => address = new SocketAddress(AddressFamily.InterNetwork, 16);

        void ISocket.GetOption(int socket, SocketOptionLevel optionLevel, SocketOptionName optionName, byte[] optionValue) {
            if (optionLevel == SocketOptionLevel.Socket && optionName == SocketOptionName.Type)
                Array.Copy(BitConverter.GetBytes((int)SocketType.Stream), optionValue, 4);
        }

        void ISocket.SetOption(int socket, SocketOptionLevel optionLevel, SocketOptionName optionName, byte[] optionValue) {

        }

        void IDns.GetHostByName(string name, out string canonicalName, out SocketAddress[] addresses) {
            var op = this.GetOperation()
                .AddParameter(name)
                .AddParameter("80")
                .AddParameter(null)
                .AddParameter("t")
                .SetCommand(SPWF04SxCommandIds.SOCKON);

            this.EnqueueOperation(op);

            var result = op.ReadString().Split(':');

            op.ReadBuffer();

            this.FinishOperation(op);

            var socket = result[0] == "On" ? int.Parse(result[2]) : throw new Exception("Request failed");

            this.CloseSocket(socket);

            canonicalName = "";
            addresses = new[] { new IPEndPoint(IPAddress.Parse(result[1]), 80).Serialize() };
        }

        public override string Id => nameof(SPWF04Sx);
        public override string Name => this.Id;
        public override string Description => string.Empty;
        public override OperationalStatus OperationalStatus => this.State == SPWF04SxWiFiState.ReadyToTransmit ? OperationalStatus.Up : OperationalStatus.Down;
        public override bool IsReceiveOnly => false;
        public override bool SupportsMulticast => false;
        public override NetworkInterfaceType NetworkInterfaceType => NetworkInterfaceType.Wireless80211;

        public override bool Supports(NetworkInterfaceComponent networkInterfaceComponent) => networkInterfaceComponent == NetworkInterfaceComponent.IPv4;
    }
}
