﻿namespace FluxUv
{
    using System;
    using System.Linq;
    using System.Net;
    using System.Runtime.Remoting.Channels;
    using System.Threading;
    using System.Threading.Tasks;
    using Uv;
    using AppFunc = System.Func< // Call
        System.Collections.Generic.IDictionary<string, object>, // Environment
                System.Threading.Tasks.Task>;
    public class FluxServer
    {
        private readonly IPAddress _ipAddress;
        private readonly int _port;
        private int _started;
        private int _stopped;
        private IntPtr _server;
        private IntPtr _loop;
        private readonly Lib.Callback _listenCallback;
        private readonly Action<Http, ArraySegment<byte>> _httpCallback;
        private readonly Action<Http> _closeCallback;
        private readonly Action<Http> _writeCallback;
        private AppFunc _app;
        private Task _task;
        private readonly Pool<Http> _httpPool = new Pool<Http>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource(1000);

        public FluxServer(int port) : this(IPAddress.Loopback, port)
        {
        }

        public FluxServer(IPAddress ipAddress, int port)
        {
            _ipAddress = ipAddress;
            _port = port;
            _listenCallback = ListenCallback;
            _httpCallback = HttpCallback;
            _writeCallback = WriteCallback;
            _closeCallback = CloseCallback;
        }

        private void CloseCallback(Http http)
        {
            _httpPool.Push(http);
        }

        public void Start(AppFunc app)
        {
            if (1 != Interlocked.Increment(ref _started)) throw new InvalidOperationException("Server is already started.");
            _app = app;

            _loop = Lib.uv_default_loop();
            _server = Pointers.Alloc(Lib.uv_handle_size(HandleType.UV_TCP));
            int error;
            if ((error = Lib.uv_tcp_init(_loop, _server)) != 0)
            {
                throw new FluxUvException("uv_tcp_init fail " + error);
            }

            var sockaddr = Lib.uv_ip4_addr(_ipAddress.ToString(), _port);
            if ((error = Lib.uv_tcp_bind(_server, sockaddr)) != 0)
            {
                throw new FluxUvException("uv_tcp_bind fail " + error);
            }

            if ((error = Lib.uv_listen(_server, 128, _listenCallback)) != 0)
            {
                throw new FluxUvException("uv_listen fail " + error);
            }

            _httpPool.Init(Enumerable.Range(0, 128).Select(_ => new Http(_loop, _server, _httpCallback, _closeCallback)), () => new Http(_loop, _server, _httpCallback, _closeCallback));

            _task = Task.Run(() => Lib.uv_run(_loop, uv_run_mode.UV_RUN_DEFAULT), _cts.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Console.Write("libuv task faulted");
                }
                else if (t.IsCanceled)
                {
                    Console.Write("libuv task cancelled");
                }
                else
                {
                    Console.Write("libuv task completed");
                }
            });
        }

        public void Stop()
        {
            if (_started == 0 || 1 != Interlocked.Increment(ref _stopped)) throw new InvalidOperationException("Server is already stopped.");
            Lib.uv_stop(_loop);
            _cts.Cancel();
        }

        private void ListenCallback(IntPtr server, int status)
        {
            if (status != 0) return;
            var http = _httpPool.Pop();
            http.Run();
        }

        private void HttpCallback(Http http, ArraySegment<byte> data)
        {
            if (data.Count == 0)
            {
                _httpPool.Push(http);
            }
            ByteRequestParser.Parse(data, http.Env);
            _app(http.Env).ContinueWith(AppCallback, http);
        }

        private void AppCallback(Task task, object state)
        {
            var http = (Http) state;
            if (task.IsFaulted)
            {
                http.Write(StockResponses.InternalServerError, _writeCallback);
            }
            else
            {
                http.WriteEnv(_writeCallback);
            }
        }

        private void WriteCallback(Http http)
        {
        }
    }
}