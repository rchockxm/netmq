﻿using System;
using System.Threading;
using JetBrains.Annotations;

namespace NetMQ
{
    /// <summary>
    /// Forwards messages bidirectionally between two sockets. You can also specify a control socket tn which proxied messages will be sent.
    /// </summary>
    /// <remarks>
    /// This class must be explicitly started by calling <see cref="Start"/>. If an external <see cref="Poller"/> has been specified,
    /// then that call will block until <see cref="Stop"/> is called.
    /// <para/>
    /// If using an external <see cref="Poller"/>, ensure the front and back end sockets have been added to it.
    /// <para/>
    /// Users of this class must call <see cref="Stop"/> when messages should no longer be proxied.
    /// </remarks>
    public class Proxy
    {
        [NotNull] private readonly NetMQSocket m_frontend;
        [NotNull] private readonly NetMQSocket m_backend;
        [CanBeNull] private readonly NetMQSocket m_controlIn;
        [CanBeNull] private readonly NetMQSocket m_controlOut;
        [CanBeNull] private Poller m_poller;
        private readonly bool m_externalPoller;

        private int m_state = StateStopped;

        private const int StateStopped = 0;
        private const int StateStarting = 1;
        private const int StateStarted = 2;
        private const int StateStopping = 3;

        /// <summary>
        /// Create a new instance of a Proxy (NetMQ.Proxy)
        /// with the given sockets to serve as a front-end, a back-end, and a control socket.
        /// </summary>
        /// <param name="frontend">the socket that messages will be forwarded from</param>
        /// <param name="backend">the socket that messages will be forwarded to</param>
        /// <param name="controlIn">this socket will have incoming messages also sent to it - you can set this to null if not needed</param>
        /// <param name="controlOut">this socket will have outgoing messages also sent to it - you can set this to null if not needed</param>
        /// <param name="poller">an optional external poller to use within this proxy</param>
        /// <exception cref="InvalidOperationException"><paramref name="poller"/> is not <c>null</c> and either <paramref name="frontend"/> or <paramref name="backend"/> are not contained within it.</exception>
        public Proxy([NotNull] NetMQSocket frontend, [NotNull] NetMQSocket backend, [NotNull] NetMQSocket controlIn, [NotNull] NetMQSocket controlOut, [CanBeNull] Poller poller = null)
        {
            if (poller != null)
            {
                if (!poller.ContainsSocket(backend) || !poller.ContainsSocket(frontend))
                    throw new InvalidOperationException("When using an external poller, both the frontend and backend sockets must be added to it.");

                m_externalPoller = true;
                m_poller = poller;
            }

            m_frontend = frontend;
            m_backend = backend;
            m_controlIn = controlIn;
            m_controlOut = controlOut ?? controlIn;
        }

        /// <summary>
        /// Create a new instance of a Proxy (NetMQ.Proxy)
        /// with the given sockets to serve as a front-end, a back-end, and a control socket.
        /// </summary>
        /// <param name="frontend">the socket that messages will be forwarded from</param>
        /// <param name="backend">the socket that messages will be forwarded to</param>
        /// <param name="control">this socket will have messages also sent to it - you can set this to null if not needed</param>
        /// <param name="poller">an optional external poller to use within this proxy</param>
        /// <exception cref="InvalidOperationException"><paramref name="poller"/> is not <c>null</c> and either <paramref name="frontend"/> or <paramref name="backend"/> are not contained within it.</exception>
        public Proxy([NotNull] NetMQSocket frontend, [NotNull] NetMQSocket backend, [CanBeNull] NetMQSocket control = null, [CanBeNull] Poller poller = null)
            :this(frontend, backend, control, null, poller)
        {}

        /// <summary>
        /// Start proxying messages between the front and back ends. Blocks, unless using an external <see cref="Poller"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">The proxy has already been started.</exception>
        public void Start()
        {
            if (Interlocked.CompareExchange(ref m_state, StateStarting, StateStopped) != StateStopped)
            {
                throw new InvalidOperationException("Proxy has already been started");
            }

            m_frontend.ReceiveReady += OnFrontendReady;
            m_backend.ReceiveReady += OnBackendReady;

            if (m_externalPoller)
            {
                m_state = StateStarted;
            }
            else
            {
                m_poller = new Poller(m_frontend, m_backend);
                m_state = StateStarted;
                m_poller.PollTillCancelled();
            }
        }

        /// <summary>
        /// Stops the proxy, blocking until the underlying <see cref="Poller"/> has completed.
        /// </summary>
        /// <exception cref="InvalidOperationException">The proxy has not been started.</exception>
        public void Stop()
        {
            if (Interlocked.CompareExchange(ref m_state, StateStopping, StateStarted) != StateStarted)
            {
                throw new InvalidOperationException("Proxy has not been started");
            }

            if (!m_externalPoller)
            {
                m_poller.CancelAndJoin();
                m_poller = null;
            }

            m_frontend.ReceiveReady -= OnFrontendReady;
            m_backend.ReceiveReady -= OnBackendReady;

            m_state = StateStopped;
        }

        private void OnFrontendReady(object sender, NetMQSocketEventArgs e)
        {
            ProxyBetween(m_frontend, m_backend, m_controlIn);
        }

        private void OnBackendReady(object sender, NetMQSocketEventArgs e)
        {
            ProxyBetween(m_backend, m_frontend, m_controlOut);
        }

        private static void ProxyBetween(NetMQSocket from, NetMQSocket to, [CanBeNull] NetMQSocket control)
        {
            var msg = new Msg();
            msg.InitEmpty();

            var copy = new Msg();
            copy.InitEmpty();

            while (true)
            {
                from.Receive(ref msg);
                var more = msg.HasMore;

                if (control != null)
                {
                    copy.Copy(ref msg);

                    control.Send(ref copy, more ? SendReceiveOptions.SendMore : SendReceiveOptions.None);
                }

                to.Send(ref msg, more ? SendReceiveOptions.SendMore : SendReceiveOptions.None);

                if (!more)
                    break;
            }

            copy.Close();
            msg.Close();
        }
    }
}
