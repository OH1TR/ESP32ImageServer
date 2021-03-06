using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ImageServer.Services
{
    public class CommandChannelService
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(CommandChannelService));

        public volatile Semaphore ShouldStop = new Semaphore(0, 1000);

        public ManualResetEvent allDone = new ManualResetEvent(false);
        public List<CommandChannelState> Connections = new List<CommandChannelState>();

        public ImageStoreService _imageStore;
        IConfiguration _config;

        public CommandChannelService(ImageStoreService ImageStore, IConfiguration config)
        {
            _imageStore = ImageStore;
            _config = config;

            Task.Run(() =>
            {
                while (!ShouldStop.WaitOne(10 * 1000))
                {
                    SendIntervalUpdates();
                }
            });
        }

        public void Stop()
        {
            ShouldStop.Release(1000);
        }

        void SendIntervalUpdates()
        {
            lock (Connections)
            {
                for (int i = Connections.Count - 1; i > -1; i--)
                {
                    if (!Connections[i].workSocket.Connected)
                        Connections.RemoveAt(i);
                }

                log.Info($"{Connections.Count} active connections");

                foreach (var c in Connections)
                {
                    try
                    {
                        int interval = _imageStore.GetUpdateInterval(c.ClientID);
                        Send(c.workSocket, $"Interval={interval}\r\n");
                        log.Debug($"Sending interval {interval} to client {c.ClientID}");
                    }
                    catch (Exception ex)
                    {
                        log.Warn(ex);
                    }
                }
            }
        }


        public void StartListening()
        {
            Socket listener = new Socket(IPAddress.Any.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                int port = int.Parse(_config["Config:CommandChannelPort"]);
                log.Info("Command channel binding to port " + port);

                listener.Bind(new IPEndPoint(IPAddress.Any, port));
                listener.Listen(100);

                while (!ShouldStop.WaitOne(0))
                {
                    // Set the event to nonsignaled state.
                    allDone.Reset();

                    // Start an asynchronous socket to listen for connections.
                    Console.WriteLine("Waiting for a connection...");
                    listener.BeginAccept(
                        new AsyncCallback(AcceptCallback),
                        listener);

                    // Wait until a connection is made before continuing.
                    allDone.WaitOne();
                }
            }
            catch (Exception e)
            {
                log.Warn(e);
            }
        }


        public void AcceptCallback(IAsyncResult ar)
        {
            Console.WriteLine("AcceptCallback");
            // Signal the main thread to continue.
            allDone.Set();

            // Get the socket that handles the client request.
            Socket listener = (Socket)ar.AsyncState;
            Socket handler = listener.EndAccept(ar);

            // Create the state object.
            CommandChannelState state = new CommandChannelState();
            state.workSocket = handler;
            lock (Connections)
            {
                Connections.Add(state);
            }
            handler.BeginReceive(state.buffer, 0, CommandChannelState.BufferSize, 0,
                new AsyncCallback(ReadCallback), state);
        }


        public void ReadCallback(IAsyncResult ar)
        {
            CommandChannelState state = (CommandChannelState)ar.AsyncState;
            Socket handler = state.workSocket;
            try
            {
                String content = String.Empty;

                int bytesRead = handler.EndReceive(ar);

                if (bytesRead > 0)
                {
                    state.sb.Append(Encoding.ASCII.GetString(state.buffer, 0, bytesRead));

                    content = state.sb.ToString();
                    int pos = content.IndexOf('\n'); //TODO something strange here with linux
                    if (pos != -1)
                    {
                        state.sb.Remove(0, pos + 1);
                        string line = content.Substring(0, pos).Replace("\r", "");
                        ProcessLineFromClient(line, state);
                    }
                }
                else
                {
                    handler.Disconnect(false);
                    return;
                }
                handler.BeginReceive(state.buffer, 0, CommandChannelState.BufferSize, 0,
                new AsyncCallback(ReadCallback), state);
            }
            catch (Exception ex)
            {
                log.Debug(ex);

                if (handler.Connected)
                    handler.Disconnect(false);

                return;
            }
        }


        private void ProcessLineFromClient(string line, CommandChannelState state)
        {
            Console.WriteLine("ProcessLineFromClient:"+line);
            if (line.StartsWith("Client-ID:"))
            {
                string id = line.Substring(10).Trim();

                Tools.ValidateClientID(id);

                state.ClientID = id.Trim();
            }
        }


        private void Send(Socket handler, String data)
        {
            // Convert the string data to byte data using ASCII encoding.
            byte[] byteData = Encoding.ASCII.GetBytes(data);

            // Begin sending the data to the remote device.
            handler.BeginSend(byteData, 0, byteData.Length, 0,
                new AsyncCallback(SendCallback), handler);
        }


        private void SendCallback(IAsyncResult ar)
        {
            try
            {
                // Retrieve the socket from the state object.
                Socket handler = (Socket)ar.AsyncState;

                // Complete sending the data to the remote device.
                int bytesSent = handler.EndSend(ar);
                Console.WriteLine("Sent {0} bytes to client.", bytesSent);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }
    }
}