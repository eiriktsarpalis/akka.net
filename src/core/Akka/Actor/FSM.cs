﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using Akka.Dispatch.SysMsg;
using Akka.Event;
using Akka.Routing;
using Akka.Util;

namespace Akka.Actor
{
    public abstract class FSMBase : ActorBase
    {
        #region States

        /// <summary>
        /// Message type which is sent directly to the subscriber Actor in <see cref="SubscribeTransitionCallBack"/>
        /// before sending any <see cref="Transition{TS}"/> messages.
        /// </summary>
        /// <typeparam name="TS">The type of the state being used in this finite state machine.</typeparam>
        public class CurrentState<TS>
        {
            public CurrentState(ActorRef fsmRef, TS state)
            {
                State = state;
                FsmRef = fsmRef;
            }

            public ActorRef FsmRef { get; private set; }

            public TS State { get; private set; }
        }

        /// <summary>
        /// Message type which is used to communicate transitions between states to all subscribed listeners
        /// (use <see cref="SubscribeTransitionCallBack"/>)
        /// </summary>
        /// <typeparam name="TS">The type of state used</typeparam>
        public class Transition<TS>
        {
            public Transition(ActorRef fsmRef, TS @from, TS to)
            {
                To = to;
                From = @from;
                FsmRef = fsmRef;
            }

            public ActorRef FsmRef { get; private set; }

            public TS From { get; private set; }

            public TS To { get; private set; }
        }

        /// <summary>
        /// Send this to an <see cref="SubscribeTransitionCallBack"/> to request first the <see cref="UnsubscribeTransitionCallBack"/>
        /// followed by a series of <see cref="Transition{TS}"/> updates. Cancel the subscription using
        /// <see cref="CurrentState{TS}"/>.
        /// </summary>
        public class SubscribeTransitionCallBack
        {
            public SubscribeTransitionCallBack(ActorRef actorRef)
            {
                ActorRef = actorRef;
            }

            public ActorRef ActorRef { get; private set; }
        }

        /// <summary>
        /// Unsubscribe from <see cref="SubscribeTransitionCallBack"/> notifications which were
        /// initializd by sending the corresponding <see cref="Transition{TS}"/>.
        /// </summary>
        public class UnsubscribeTransitionCallBack
        {
            public UnsubscribeTransitionCallBack(ActorRef actorRef)
            {
                ActorRef = actorRef;
            }

            public ActorRef ActorRef { get; private set; }
        }

        /// <summary>
        /// Reason why this <see cref="FSM{T,S}"/> is shutting down
        /// </summary>
        public abstract class Reason { }

        /// <summary>
        /// Default <see cref="Reason"/> if calling Stop().
        /// </summary>
        public class Normal : Reason { }

        /// <summary>
        /// Reason given when someone as calling <see cref="Stop"/> from outside;
        /// also applies to <see cref="ActorSystem"/> supervision directive.
        /// </summary>
        public class Shutdown : Reason { }

        /// <summary>
        /// Signifies that the <see cref="FSM{T,S}"/> is shutting itself down because of an error,
        /// e.g. if the state to transition into does not exist. You can use this to communicate a more
        /// precise cause to the <see cref="FSM{T,S}.OnTermination"/> block.
        /// </summary>
        public class Failure : Reason
        {
            public Failure(object cause)
            {
                Cause = cause;
            }

            public object Cause { get; private set; }
        }

        /// <summary>
        /// Used in the event of a timeout between transitions
        /// </summary>
        public class StateTimeout { }

        /*
         * INTERNAL API - used for ensuring that state changes occur on-time
         */

        internal class TimeoutMarker
        {
            public TimeoutMarker(long generation)
            {
                Generation = generation;
            }

            public long Generation { get; private set; }
        }
        [DebuggerDisplay("Timer {Name,nq}, message: {Message")]
        internal class Timer : NoSerializationVerificationNeeded
        {
            private readonly LoggingAdapter _debugLog;

            public Timer(string name, object message, bool repeat, int generation, IActorContext context, LoggingAdapter debugLog)
            {
                _debugLog = debugLog;
                Context = context;
                Generation = generation;
                Repeat = repeat;
                Message = message;
                Name = name;
                _scheduler = context.System.Scheduler;
                _ref = new CancellationTokenSource();
            }

            private readonly Scheduler _scheduler;
            private readonly CancellationTokenSource _ref;

            public string Name { get; private set; }

            public object Message { get; private set; }

            public bool Repeat { get; private set; }

            public int Generation { get; private set; }

            public IActorContext Context { get; private set; }

            public void Schedule(ActorRef actor, TimeSpan timeout)
            {
                var name = Name;
                var message = Message;

                Action send;
                if(_debugLog != null)
                    send = () =>
                    {
                        _debugLog.Debug("{3}Timer '{0}' went off. Sending {1} -> {2}", name, message, actor,
                            _ref.IsCancellationRequested ? "Cancelled " : "");
                        actor.Tell(this, Context.Self);
                    };
                else
                    send = () => actor.Tell(this, Context.Self);

                if(Repeat) _scheduler.Schedule(timeout, timeout, send, _ref.Token);
                else _scheduler.ScheduleOnce(timeout, send, _ref.Token);
            }

            public void Cancel()
            {
                if (!_ref.IsCancellationRequested)
                {
                    _ref.Cancel(false);
                }
            }
        }

        /// <summary>
        /// Log entry of the <see cref="LoggingFSM"/> - can be obtained by calling <see cref="GetLog"/>
        /// </summary>
        /// <typeparam name="TS">The name of the state</typeparam>
        /// <typeparam name="TD">The data of the state</typeparam>
        public class LogEntry<TS, TD>
        {
            public LogEntry(TS stateName, TD stateData, object fsmEvent)
            {
                FsmEvent = fsmEvent;
                StateData = stateData;
                StateName = stateName;
            }

            public TS StateName { get; private set; }

            public TD StateData { get; private set; }

            public object FsmEvent { get; private set; }
        }

        /// <summary>
        /// This captures all of the managed state of the <see cref="FSM{T,S}"/>: the state name,
        /// the state data, possibly custom timeout, stop reason, and repleis accumulated while
        /// processing the last message.
        /// </summary>
        /// <typeparam name="TS">The name of the state</typeparam>
        /// <typeparam name="TD">The data of the state</typeparam>
        public class State<TS, TD> : IEquatable<State<TS, TD>>
        {
            public State(TS stateName, TD stateData, TimeSpan? timeout = null, Reason stopReason = null, List<object> replies = null)
            {
                Replies = replies ?? new List<object>();
                StopReason = stopReason;
                Timeout = timeout;
                StateData = stateData;
                StateName = stateName;
            }

            public TS StateName { get; private set; }

            public TD StateData { get; private set; }

            public TimeSpan? Timeout { get; private set; }

            public Reason StopReason { get; private set; }

            public List<object> Replies { get; private set; }

            public State<TS, TD> Copy(TimeSpan? timeout, Reason stopReason = null, List<object> replies = null)
            {
                return new State<TS, TD>(StateName, StateData, timeout, stopReason ?? StopReason, replies ?? Replies);
            }

            /// <summary>
            /// Modify the state transition descriptor to include a state timeout for the 
            /// next state. This timeout overrides any default timeout set for the next state.
            /// <remarks>Use <see cref="TimeSpan.MaxValue"/> to cancel a timeout.</remarks>
            /// </summary>
            public State<TS, TD> ForMax(TimeSpan timeout)
            {
                if (timeout <= TimeSpan.MaxValue) return Copy(timeout);
                return Copy(timeout: null);
            }

            /// <summary>
            /// Send reply to sender of the current message, if available.
            /// </summary>
            /// <param name="replyValue"></param>
            /// <returns></returns>
            public State<TS, TD> Replying(object replyValue)
            {
                if (Replies == null) Replies = new List<object>();
                var newReplies = Replies.ToArray().ToList();
                newReplies.Add(replyValue);
                return Copy(Timeout, replies: newReplies);
            }

            /// <summary>
            /// Modify state transition descriptor with new state data. The data will be set
            /// when transitioning to the new state.
            /// </summary>
            public State<TS, TD> Using(TD nextStateData)
            {
                return new State<TS, TD>(StateName, nextStateData, Timeout, StopReason, Replies);
            }

            /// <summary>
            /// INTERNAL API
            /// </summary>
            internal State<TS, TD> WithStopReason(Reason reason)
            {
                return Copy(Timeout, reason);
            }

            public override string ToString()
            {
                return StateName + ", " + StateData;
            }

            public bool Equals(State<TS, TD> other)
            {
                if(ReferenceEquals(null, other)) return false;
                if(ReferenceEquals(this, other)) return true;
                return EqualityComparer<TS>.Default.Equals(StateName, other.StateName) && EqualityComparer<TD>.Default.Equals(StateData, other.StateData) && Timeout.Equals(other.Timeout) && Equals(StopReason, other.StopReason) && Equals(Replies, other.Replies);
            }

            public override bool Equals(object obj)
            {
                if(ReferenceEquals(null, obj)) return false;
                if(ReferenceEquals(this, obj)) return true;
                if(obj.GetType() != GetType()) return false;
                return Equals((State<TS, TD>)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hashCode = EqualityComparer<TS>.Default.GetHashCode(StateName);
                    hashCode = (hashCode * 397) ^ EqualityComparer<TD>.Default.GetHashCode(StateData);
                    hashCode = (hashCode * 397) ^ Timeout.GetHashCode();
                    hashCode = (hashCode * 397) ^ (StopReason != null ? StopReason.GetHashCode() : 0);
                    hashCode = (hashCode * 397) ^ (Replies != null ? Replies.GetHashCode() : 0);
                    return hashCode;
                }
            }
        }

        /// <summary>
        /// All messages sent to the <see cref="FSM{TS,TD}"/> will be wrapped inside an <see cref="Event{TD}"/>,
        /// which allows pattern matching to extract both state and data.
        /// </summary>
        /// <typeparam name="TD">The state data for this event</typeparam>
        public class Event<TD> : NoSerializationVerificationNeeded
        {
            public Event(object fsmEvent, TD stateData)
            {
                StateData = stateData;
                FsmEvent = fsmEvent;
            }

            public object FsmEvent { get; private set; }

            public TD StateData { get; private set; }

            public override string ToString()
            {
                return "Event: <" + FsmEvent + ">, StateData: <" + StateData + ">";
            }
        }

        /// <summary>
        /// Class representing the state of the <see cref="FSM{TS,TD}"/> within the OnTermination block.
        /// </summary>
        public class StopEvent<TS, TD> : NoSerializationVerificationNeeded
        {
            public StopEvent(Reason reason, TS terminatedState, TD stateData)
            {
                StateData = stateData;
                TerminatedState = terminatedState;
                Reason = reason;
            }

            public Reason Reason { get; private set; }

            public TS TerminatedState { get; private set; }

            public TD StateData { get; private set; }
        }

        #endregion
    }

    /// <summary>
    /// Finite state machine (FSM) actor.
    /// </summary>
    /// <typeparam name="TS">The state name type</typeparam>
    /// <typeparam name="TD">The state data type</typeparam>
    public abstract class FSM<TS, TD> : FSMBase, IActorLogging, IListeners
    {
        protected FSM()
        {
            if(this is LoggingFSM)
                DebugEvent = Context.System.Settings.FsmDebugEvent;
        }
        public delegate State<TS, TD> StateFunction(Event<TD> fsmEvent);

        public delegate void TransitionHandler(TS initialState, TS nextState);

        protected readonly LoggingAdapter _log = Logging.GetLogger(Context);
        public LoggingAdapter Log { get { return _log; } }

        #region Finite State Machine Domain Specific Language (FSM DSL if you like acronyms)

        /// <summary>
        /// Insert a new <see cref="StateFunction"/> at the end of the processing chain for the
        /// given state. If the stateTimeout parameter is set, entering this state without a
        /// differing explicit timeout setting will trigger a <see cref="FSMBase.StateTimeout"/>.
        /// </summary>
        /// <param name="stateName">designator for the state</param>
        /// <param name="func">delegate describing this state's response to input</param>
        /// <param name="timeout">default timeout for this state</param>
        public void When(TS stateName, StateFunction func, TimeSpan? timeout = null)
        {
            Register(stateName, func, timeout);
        }

        /// <summary>
        /// Sets the initial state for this FSM. Call this method from the constructor before the <see cref="Initialize"/> method.
        /// If different state is needed after a restart this method, followed by <see cref="Initialize"/>, can be used in the actor
        /// life cycle hooks <see cref="ActorBase.PreStart()"/> and <see cref="ActorBase.PostRestart"/>.
        /// </summary>
        /// <param name="stateName">Initial state designator.</param>
        /// <param name="stateData">Initial state data.</param>
        /// <param name="timeout">State timeout for the initial state, overriding the default timeout for that state.</param>
        public void StartWith(TS stateName, TD stateData, TimeSpan? timeout = null)
        {
            _currentState = new State<TS, TD>(stateName, stateData, timeout);
        }

        /// <summary>
        /// Produce transition to other state. Return this from a state function
        /// in order to effect the transition.
        /// </summary>
        /// <param name="nextStateName">State designator for the next state</param>
        /// <returns>State transition descriptor</returns>
        public State<TS, TD> GoTo(TS nextStateName)
        {
            return new State<TS, TD>(nextStateName, _currentState.StateData);
        }

        /// <summary>
        /// Produce "empty" transition descriptor. Return this from a state function
        /// when no state change is to be effected.
        /// </summary>
        /// <returns>Descriptor for staying in the current state.</returns>
        public State<TS, TD> Stay()
        {
            return GoTo(_currentState.StateName);
        }

        /// <summary>
        /// Produce change descriptor to stop this FSM actor with <see cref="FSMBase.Reason"/> <see cref="FSMBase.Normal"/>
        /// </summary>
        public State<TS, TD> Stop()
        {
            return Stop(new Normal());
        }

        /// <summary>
        /// Produce change descriptor to stop this FSM actor with the specified <see cref="FSMBase.Reason"/>.
        /// </summary>
        public State<TS, TD> Stop(Reason reason)
        {
            return Stop(reason, _currentState.StateData);
        }

        public State<TS, TD> Stop(Reason reason, TD stateData)
        {
            return Stay().Using(stateData).WithStopReason(reason);
        }

        public sealed class TransformHelper
        {
            public TransformHelper(StateFunction func)
            {
                Func = func;
            }

            public StateFunction Func { get; private set; }

            public StateFunction Using(Func<State<TS, TD>, State<TS, TD>> andThen)
            {
                StateFunction continuedDelegate = @event => andThen.Invoke(Func.Invoke(@event));
                return continuedDelegate;
            }
        }

        /// <summary>
        /// Schedule named timer to deliver message after given delay, possibly repeating.
        /// Any existing timer with the same name will automatically be canceled before adding
        /// the new timer.
        /// </summary>
        /// <param name="name">identifier to be used with <see cref="CancelTimer"/>.</param>
        /// <param name="msg">message to be delivered</param>
        /// <param name="timeout">delay of first message delivery and between subsequent messages.</param>
        /// <param name="repeat">send once if false, scheduleAtFixedRate if true</param>
        public void SetTimer(string name, object msg, TimeSpan timeout, bool repeat = false)
        {
            if(DebugEvent)
                Log.Debug("setting " + (repeat ? "repeating" : "") + "timer '{0}' / {1}: {2}", name, timeout, msg);
            if(_timers.ContainsKey(name))
                _timers[name].Cancel();
            var timer = new Timer(name, msg, repeat, _timerGen.Next, Context, DebugEvent ? Log : null);
            timer.Schedule(Self, timeout);

            if (!_timers.ContainsKey(name))
                _timers.Add(name, timer);
            else
                _timers[name] = timer;
        }

        /// <summary>
        /// Cancel a named <see cref="Timer"/>, ensuring that the message is not subsequently delivered (no race.)
        /// </summary>
        /// <param name="name">The name of the timer to cancel.</param>
        public void CancelTimer(string name)
        {
            if (DebugEvent)
            {
                Log.Debug("Cancelling timer {0}", name);
            }

            if (_timers.ContainsKey(name))
            {
                _timers[name].Cancel();
                _timers.Remove(name);
            }
        }

        /// <summary>
        /// Determines whether the named timer is still active. Returns true 
        /// unless the timer does not exist, has previously been cancelled, or
        /// if it was a single-shot timer whose message was already received.
        /// </summary>
        public bool IsTimerActive(string name)
        {
            return _timers.ContainsKey(name);
        }

        /// <summary>
        /// Set the state timeout explicitly. This method can be safely used from
        /// within a state handler.
        /// </summary>
        public void SetStateTimeout(TS state, TimeSpan? timeout)
        {
            if(!_stateTimeouts.ContainsKey(state))
                _stateTimeouts.Add(state, timeout);
            else
                _stateTimeouts[state] = timeout;
        }

        /// <summary>
        /// INTERNAL API. Used for testing.
        /// </summary>
        internal bool IsStateTimerActive
        {
            get
            {
                return _timeoutFuture != null;
            }
        }

        /// <summary>
        /// Set handler which is called upon each state transition, i.e. not when
        /// staying in the same state. 
        /// </summary>
        public void OnTransition(TransitionHandler transitionHandler)
        {
            transitionEvent.Add(transitionHandler);
        }

        /// <summary>
        /// Set the handler which is called upon termination of this FSM actor. Calling this
        /// method again will overwrite the previous contents.
        /// </summary>
        public void OnTermination(Action<StopEvent<TS, TD>> terminationHandler)
        {
            terminateEvent = terminationHandler;
        }

        /// <summary>
        /// Set handler which i called upon reception of unhandled FSM messages. Calling
        /// this method again will overwrite the previous contents.
        /// </summary>
        /// <param name="stateFunction"></param>
        public void WhenUnhandled(StateFunction stateFunction)
        {
            handleEvent = OrElse(stateFunction, HandleEventDefault);
        }

        /// <summary>
        /// Verify the existence of initial state and setup timers. This should be the
        /// last call within the constructor or <see cref="ActorBase.PreStart"/> and
        /// <see cref="ActorBase.PostRestart"/>.
        /// </summary>
        public void Initialize()
        {
            MakeTransition(_currentState);
        }

        /// <summary>
        /// Current state name
        /// </summary>
        public TS StateName
        {
            get { return _currentState.StateName; }
        }

        /// <summary>
        /// Current state data
        /// </summary>
        public TD StateData
        {
            get { return _currentState.StateData; }
        }

        /// <summary>
        /// Return next state data (available in <see cref="OnTransition"/> handlers)
        /// </summary>
        public TD NextStateData
        {
            get
            {
                if(_nextState == null) throw new InvalidOperationException("NextStateData is only available during OnTransition");
                return _nextState.StateData;
            }
        }

        public TransformHelper Transform(StateFunction func) { return new TransformHelper(func); }

        #endregion

        #region Internal implementation details

        private readonly ListenerSupport _listener = new ListenerSupport();
        public ListenerSupport Listeners { get { return _listener; } }

        /// <summary>
        /// Can be set to enable debugging on certain actions taken by the FSM
        /// </summary>
        protected bool DebugEvent;

        /// <summary>
        /// FSM state data and current timeout handling
        /// </summary>
        private State<TS, TD> _currentState;

        private CancellationTokenSource _timeoutFuture;
        private State<TS, TD> _nextState;
        private long _generation = 0L;

        /// <summary>
        /// Timer handling
        /// </summary>
        private readonly IDictionary<string, Timer> _timers = new Dictionary<string, Timer>();
        private readonly AtomicCounter _timerGen = new AtomicCounter(0);

        /// <summary>
        /// State definitions
        /// </summary>
        private readonly Dictionary<TS, StateFunction> _stateFunctions = new Dictionary<TS, StateFunction>();
        private readonly Dictionary<TS, TimeSpan?> _stateTimeouts = new Dictionary<TS, TimeSpan?>();

        private void Register(TS name, StateFunction function, TimeSpan? timeout)
        {
            if (_stateFunctions.ContainsKey(name))
            {
                _stateFunctions[name] = OrElse(_stateFunctions[name], function);
                _stateTimeouts[name] = _stateTimeouts[name] ?? timeout;
            }
            else
            {
                _stateFunctions.Add(name, function);
                _stateTimeouts.Add(name, timeout);
            }
        }

        /// <summary>
        /// Unhandled event handler
        /// </summary>
        private StateFunction HandleEventDefault
        {
            get
            {
                return delegate(Event<TD> @event)
                {
                    Log.Warn(String.Format("unhandled event {0} in state {1}", @event.FsmEvent, StateName));
                    return Stay();
                };
            }
        }

        private StateFunction _handleEvent;

        private StateFunction handleEvent
        {
            get { return _handleEvent ?? (_handleEvent = HandleEventDefault); }
            set { _handleEvent = value; }
        }
        

        /// <summary>
        /// Termination handling
        /// </summary>
        private Action<StopEvent<TS, TD>> terminateEvent = @event =>
        {

        };

        /// <summary>
        /// Transition handling
        /// </summary>
        private readonly IList<TransitionHandler> transitionEvent = new List<TransitionHandler>();

        private void HandleTransition(TS previous, TS next)
        {
            foreach (var tran in transitionEvent)
            {
                tran.Invoke(previous, next);
            }
        }

        /// <summary>
        /// C# port of Scala's orElse method for partial function chaining.
        /// 
        /// See http://scalachina.com/api/scala/PartialFunction.html
        /// </summary>
        /// <param name="original">The original <see cref="StateFunction"/> to be called</param>
        /// <param name="fallback">The <see cref="StateFunction"/> to be called if <see cref="original"/> returns null</param>
        /// <returns>A <see cref="StateFunction"/> which combines both the results of <see cref="original"/> and <see cref="fallback"/></returns>
        private static StateFunction OrElse(StateFunction original, StateFunction fallback)
        {
            StateFunction chained = delegate(Event<TD> @event)
            {
                var originalResult = original.Invoke(@event);
                if (originalResult == null) return fallback.Invoke(@event);
                return originalResult;
            };

            return chained;
        }

        #endregion

        #region Actor methods

        /// <summary>
        /// Main actor receive method
        /// </summary>
        /// <param name="message"></param>
        protected override bool Receive(object message)
        {
            var match = PatternMatch.Match(message)
                .With<TimeoutMarker>(marker =>
                {
                    if (_generation == marker.Generation)
                    {
                        ProcessMsg(new StateTimeout(), "state timeout");
                    }
                })
                .With<Timer>(t =>
                {
                    if (_timers.ContainsKey(t.Name) && _timers[t.Name].Generation == t.Generation)
                    {
                        if (_timeoutFuture != null)
                        {
                            _timeoutFuture.Cancel(false);
                            _timeoutFuture = null;
                        }
                        _generation++;
                        if (!t.Repeat)
                        {
                            _timers.Remove(t.Name);
                        }
                        ProcessMsg(t.Message,t);
                    }
                })
                .With<SubscribeTransitionCallBack>(cb =>
                {
                    Context.Watch(cb.ActorRef);
                    Listeners.Add(cb.ActorRef);
                    //send the current state back as a reference point
                    cb.ActorRef.Tell(new CurrentState<TS>(Self, _currentState.StateName));
                })
                .With<Listen>(l =>
                {
                    Context.Watch(l.Listener);
                    Listeners.Add(l.Listener);
                    l.Listener.Tell(new CurrentState<TS>(Self, _currentState.StateName));
                })
                .With<UnsubscribeTransitionCallBack>(ucb =>
                {
                    Context.Unwatch(ucb.ActorRef);
                    Listeners.Remove(ucb.ActorRef);
                })
                .With<Deafen>(d =>
                {
                    Context.Unwatch(d.Listener);
                    Listeners.Remove(d.Listener);
                })
                .With<Terminated>(t => Listeners.Remove(t.ActorRef))
                .Default(msg =>
                {
                    if (_timeoutFuture != null)
                    {
                        _timeoutFuture.Cancel(false);
                        _timeoutFuture = null;
                    }
                    _generation++;
                    ProcessMsg(msg, Sender);
                });
            return match.WasHandled;
        }

        private void ProcessMsg(object any, object source)
        {
            var fsmEvent = new Event<TD>(any, _currentState.StateData);
            ProcessEvent(fsmEvent, source);
        }

        private void ProcessEvent(Event<TD> fsmEvent, object source)
        {
            if(DebugEvent)
            {
                var srcStr = GetSourceString(source);
                Log.Debug("processing {0} from {1}", fsmEvent, srcStr);
            }
            var stateFunc = _stateFunctions[_currentState.StateName];
            var oldState = _currentState;
            State<TS, TD> upcomingState = null;

            if(stateFunc != null)
            {
                upcomingState = stateFunc(fsmEvent);
            }

            if(upcomingState == null)
            {
                upcomingState = handleEvent(fsmEvent);
            }

            ApplyState(upcomingState);
            if(DebugEvent && oldState != upcomingState)
            {
                Log.Debug("transition {0} -> {1}", oldState, upcomingState);
            }
        }

        private string GetSourceString(object source)
        {
            var s = source as string;
            if(s != null) return s;
            var timer = source as Timer;
            if(timer != null) return "timer '" + timer.Name + "'";
            var actorRef = source as ActorRef;
            if(actorRef != null) return actorRef.ToString();
            return "unknown";
        }

        private void ApplyState(State<TS, TD> upcomingState)
        {
            if (upcomingState.StopReason == null){ MakeTransition(upcomingState);
                return;
            }
            var replies = upcomingState.Replies;
            replies.Reverse();
            foreach (var reply in replies)
            {
                Sender.Tell(reply);
            }
            Terminate(upcomingState);
            Context.Stop(Self);
        }

        private void MakeTransition(State<TS, TD> upcomingState)
        {
            if (!_stateFunctions.ContainsKey(upcomingState.StateName))
            {
                Terminate(
                    Stay()
                        .WithStopReason(
                            new Failure(String.Format((string) "Next state {0} does not exist", (object) upcomingState.StateName))));
            }
            else
            {
                var replies = upcomingState.Replies;
                replies.Reverse();
                foreach (var r in replies) { Sender.Tell(r); }
                if (!_currentState.StateName.Equals(upcomingState.StateName))
                {
                    _nextState = upcomingState;
                    HandleTransition(_currentState.StateName, _nextState.StateName);
                    Listeners.Gossip(new Transition<TS>(Self, _currentState.StateName, _nextState.StateName));
                    _nextState = null;
                }
                _currentState = upcomingState;
                var timeout = _currentState.Timeout ?? _stateTimeouts[_currentState.StateName];
                if (timeout.HasValue)
                {
                    var t = timeout.Value;
                    if (t < TimeSpan.MaxValue)
                    {
                        _timeoutFuture = new CancellationTokenSource();
                        Context.System.Scheduler.ScheduleOnce(t, Self,
                            new TimeoutMarker(_generation), _timeoutFuture.Token);
                    }
                }
            }
        }

        private void Terminate(State<TS, TD> upcomingState)
        {
            if (_currentState.StopReason == null)
            {
                var reason = upcomingState.StopReason;
                LogTermination(reason);
                foreach (var t in _timers) { t.Value.Cancel(); }
                _timers.Clear();
                _currentState = upcomingState;

                var stopEvent = new StopEvent<TS, TD>(reason, _currentState.StateName, _currentState.StateData);
                terminateEvent(stopEvent);
            }
        }

        /// <summary>
        /// Call the <see cref="OnTermination"/> hook if you want to retain this behavior.
        /// When overriding make sure to call base.PostStop();
        /// 
        /// Please note that this method is called by default from <see cref="ActorBase.PreRestart"/> so
        /// override that one if <see cref="OnTermination"/> shall not be called during restart.
        /// </summary>
        protected override void PostStop()
        {
            /*
             * Setting this instance's state to Terminated does no harm during restart, since
             * the new instance will initialize fresh using StartWith.
             */
            Terminate(Stay().WithStopReason(new Shutdown()));
            base.PostStop();
        }

        #endregion

        /// <summary>
        /// By default, <see cref="Failure"/> is logged at error level and other
        /// reason types are not logged. It is possible to override this behavior.
        /// </summary>
        /// <param name="reason"></param>
        protected virtual void LogTermination(Reason reason)
        {
            PatternMatch.Match(reason)
                .With<Failure>(f =>
                {
                    if (f.Cause is Exception)
                    {
                        Log.Error(f.Cause.AsInstanceOf<Exception>(), "terminating due to Failure");
                    }
                    else
                    {
                        Log.Error(f.Cause.ToString());
                    }
                });
        }
    }

    /// <summary>
    /// Marker interface to let the setting "akka.actor.debug.fsm" control if logging should occur in <see cref="FSM{TS,TD}"/>
    /// </summary>
    public interface LoggingFSM { }
}
