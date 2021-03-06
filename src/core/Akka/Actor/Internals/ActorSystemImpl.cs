﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Akka.Configuration;
using Akka.Dispatch;
using Akka.Dispatch.SysMsg;
using Akka.Event;


namespace Akka.Actor.Internals
{
    /// <summary>
    /// <remarks>Note! Part of internal API. Breaking changes may occur without notice. Use at own risk.</remarks>
    /// </summary>
    public class ActorSystemImpl : ExtendedActorSystem
    {
        private ActorRef _logDeadLetterListener;
        private readonly ConcurrentDictionary<Type, object> _extensions = new ConcurrentDictionary<Type, object>();

        private LoggingAdapter _log;
        private ActorRefProvider _provider;
        private Settings _settings;
        private readonly string _name;
        private Serialization.Serialization _serialization;
        private EventStream _eventStream;
        private Dispatchers _dispatchers;
        private Mailboxes _mailboxes;
        private Scheduler _scheduler;

        public ActorSystemImpl(string name)
            : this(name, ConfigurationFactory.Load())
        {
        }
        public ActorSystemImpl(string name, Config config)
        {
            if(!Regex.Match(name, "^[a-zA-Z0-9][a-zA-Z0-9-]*$").Success)
                throw new ArgumentException(
                    "invalid ActorSystem name [" + name +
                    "], must contain only word characters (i.e. [a-zA-Z0-9] plus non-leading '-')");
            if(config == null)
                throw new ArgumentNullException("config");
            _name = name;
            ConfigureScheduler();
            ConfigureSettings(config);
            ConfigureEventStream();
            ConfigureSerialization();
            ConfigureProvider();
            ConfigureMailboxes();
            ConfigureDispatchers();
        }

        public override ActorRefProvider Provider { get { return _provider; } }
        public override Settings Settings { get { return _settings; } }
        public override string Name { get { return _name; } }
        public override Serialization.Serialization Serialization { get { return _serialization; } }
        public override EventStream EventStream { get { return _eventStream; } }
        public override ActorRef DeadLetters { get { return Provider.DeadLetters; } }
        public override Dispatchers Dispatchers { get { return _dispatchers; } }
        public override Mailboxes Mailboxes { get { return _mailboxes; } }
        public override Scheduler Scheduler { get { return _scheduler; } }
        public override LoggingAdapter Log { get { return _log; } }


        public override InternalActorRef Guardian { get { return _provider.Guardian; } }
        public override InternalActorRef SystemGuardian { get { return _provider.SystemGuardian; } }

        /// <summary>Creates a new system actor.</summary>
        public override ActorRef SystemActorOf(Props props, string name = null)
        {
            return _provider.SystemGuardian.Cell.ActorOf(props, name: name);
        }

        /// <summary>Creates a new system actor.</summary>
        public override ActorRef SystemActorOf<TActor>(string name = null)
        {
            return _provider.SystemGuardian.Cell.ActorOf<TActor>(name);
        }

        /// <summary>Starts this system</summary>
        public void Start()
        {
            _provider.Init(this);
            ConfigureLoggers();
            LoadExtensions();

            if(_settings.LogDeadLetters > 0)
                _logDeadLetterListener = SystemActorOf<DeadLetterListener>("deadLetterListener");


            if(_settings.LogConfigOnStart)
            {
                _log.Warn(Settings.ToString());
            }
        }

        public override ActorRef ActorOf(Props props, string name = null)
        {
            return _provider.Guardian.Cell.ActorOf(props, name: name);
        }


        public override ActorSelection ActorSelection(ActorPath actorPath)
        {
            return ActorRefFactoryShared.ActorSelection(actorPath, this);
        }

        public override ActorSelection ActorSelection(string actorPath)
        {
            return ActorRefFactoryShared.ActorSelection(actorPath, this, _provider.RootGuardian);
        }

        private void ConfigureScheduler()
        {
            _scheduler = new Scheduler();
        }

        /// <summary>
        /// Load all of the extensions registered in the <see cref="ActorSystem.Settings"/>
        /// </summary>
        private void LoadExtensions()
        {
            var extensions = new List<IExtensionId>();
            foreach(var extensionFqn in _settings.Config.GetStringList("akka.extensions"))
            {
                var extensionType = Type.GetType(extensionFqn);
                if(extensionType == null || !typeof(IExtensionId).IsAssignableFrom(extensionType) || extensionType.IsAbstract || !extensionType.IsClass)
                {
                    _log.Error("[{0}] is not an 'ExtensionId', skipping...", extensionFqn);
                    continue;
                }

                try
                {
                    var extension = (IExtensionId)Activator.CreateInstance(extensionType);
                    extensions.Add(extension);
                }
                catch(Exception ex)
                {
                    _log.Error(ex, "While trying to load extension [{0}], skipping...", extensionFqn);
                }

            }

            ConfigureExtensions(extensions);
        }

        private void ConfigureExtensions(IEnumerable<IExtensionId> extensionIdProviders)
        {
            foreach(var extensionId in extensionIdProviders)
            {
                RegisterExtension(extensionId);
            }
        }

        public override object RegisterExtension(IExtensionId extension)
        {
            if(extension == null) return null;
            if(!_extensions.ContainsKey(extension.ExtensionType))
            {
                _extensions.TryAdd(extension.ExtensionType, extension.CreateExtension(this));
            }

            return extension.Get(this);
        }

        public override object GetExtension(IExtensionId extensionId)
        {
            object extension;
            TryGetExtension(extensionId.ExtensionType, out extension);
            return extension;
        }

        public override bool TryGetExtension(Type extensionType, out object extension)
        {
            var wasFound = _extensions.TryGetValue(extensionType, out extension);
            return wasFound;
        }

        public override bool TryGetExtension<T>(out T extension)
        {
            object item;
            var wasFound = _extensions.TryGetValue(typeof(T), out item);
            extension = item as T;
            return wasFound;
        }

        public override T GetExtension<T>()
        {
            T extension;
            TryGetExtension(out extension);
            return extension;
        }

        public override bool HasExtension(Type t)
        {
            if(typeof(IExtension).IsAssignableFrom(t))
            {
                return _extensions.ContainsKey(t);
            }
            return false;
        }

        public override bool HasExtension<T>()
        {
            return _extensions.ContainsKey(typeof(T));
        }

        /// <summary>
        ///     Configures the settings.
        /// </summary>
        /// <param name="config">The configuration.</param>
        private void ConfigureSettings(Config config)
        {
            _settings = new Settings(this, config);
        }

        /// <summary>
        ///     Configures the event stream.
        /// </summary>
        private void ConfigureEventStream()
        {
            _eventStream = new EventStream(_settings.DebugEventStream);
            _eventStream.StartStdoutLogger(_settings);
        }

        /// <summary>
        ///     Configures the serialization.
        /// </summary>
        private void ConfigureSerialization()
        {
            _serialization = new Serialization.Serialization(this);
        }

        /// <summary>
        ///     Configures the mailboxes.
        /// </summary>
        private void ConfigureMailboxes()
        {
            _mailboxes = new Mailboxes(this);
        }

        /// <summary>
        ///     Configures the provider.
        /// </summary>
        private void ConfigureProvider()
        {
            Type providerType = Type.GetType(_settings.ProviderClass);
            global::System.Diagnostics.Debug.Assert(providerType != null, "providerType != null");
            var provider = (ActorRefProvider)Activator.CreateInstance(providerType, _name, _settings, _eventStream);
            _provider = provider;
        }

        /// <summary>
        /// Extensions depends on loggers being configured before Start() is called
        /// </summary>
        private void ConfigureLoggers()
        {
            _log = new BusLogging(_eventStream, "ActorSystem(" + _name + ")", GetType());
        }

        /// <summary>
        ///     Configures the dispatchers.
        /// </summary>
        private void ConfigureDispatchers()
        {
            _dispatchers = new Dispatchers(this);
        }

        /// <summary>
        ///     Stop this actor system. This will stop the guardian actor, which in turn
        ///     will recursively stop all its child actors, then the system guardian
        ///     (below which the logging actors reside) and the execute all registered
        ///     termination handlers (<see cref="ActorSystem.RegisterOnTermination" />).
        /// </summary>
        public override void Shutdown()
        {
            _provider.Guardian.Stop();
        }


        public override Task TerminationTask { get { return _provider.TerminationTask; } }

        public override void AwaitTermination()
        {
            AwaitTermination(Timeout.InfiniteTimeSpan, CancellationToken.None);
        }

        public override bool AwaitTermination(TimeSpan timeout)
        {
            return AwaitTermination(timeout, CancellationToken.None);
        }

        public override bool AwaitTermination(TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                return _provider.TerminationTask.Wait((int) timeout.TotalMilliseconds, cancellationToken);
            }
            catch(OperationCanceledException)
            {
                //The cancellationToken was canceled.
                return false;
            }
        }

        public override void Stop(ActorRef actor)
        {
            var path = actor.Path;
            var parentPath = path.Parent;
            if(parentPath == _provider.Guardian.Path)
                _provider.Guardian.Tell(new StopChild(actor));
            else if(parentPath == _provider.SystemGuardian.Path)
                _provider.SystemGuardian.Tell(new StopChild(actor));
            else
                ((InternalActorRef)actor).Stop();
        }


    }
}