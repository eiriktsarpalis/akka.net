﻿using Akka.Actor;
using Akka.Routing;
using Akka.TestKit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka.Util;
using Xunit;

namespace Akka.Tests.Routing
{
    public class BroadcastSpec : AkkaSpec
    {

        public new class TestActor : UntypedActor
        {
            protected override void OnReceive(object message)
            {

            }
        }

        public class BroadcastTarget : UntypedActor
        {
            private AtomicCounter _counter;
            private TestLatch _latch;
            public BroadcastTarget(TestLatch latch, AtomicCounter counter)
            {
                _latch = latch;
                _counter = counter;
            }
            protected override void OnReceive(object message)
            {
                if (message is string)
                {
                    var s = (string)message;
                    if (s == "end")
                    {
                        _latch.CountDown();
                    }
                }
                if (message is int)
                {
                    var i = (int)message;
                    _counter.GetAndAdd(i);
                }
            }
        }


        [Fact]
        public void BroadcastGroup_router_must_broadcast_message_using_Tell()
        {
            var doneLatch = new TestLatch(Sys, 2);
            var counter1 = new AtomicCounter(0);
            var counter2 = new AtomicCounter(0);
            var actor1 = Sys.ActorOf(Props.Create(() => new BroadcastTarget(doneLatch, counter1)));
            var actor2 = Sys.ActorOf(Props.Create(() => new BroadcastTarget(doneLatch, counter2)));

            var routedActor = Sys.ActorOf(Props.Create<TestActor>().WithRouter(new BroadcastGroup(actor1.Path.ToString(), actor2.Path.ToString())));
            routedActor.Tell(new Broadcast(1));
            routedActor.Tell(new Broadcast("end"));

            doneLatch.Ready(TimeSpan.FromSeconds(1));

            counter1.Current.ShouldBe(1);
            counter2.Current.ShouldBe(1);
        }

        [Fact]
        public void BroadcastGroup_router_must_broadcast_message_using_Ask()
        {
            var doneLatch = new TestLatch(Sys, 2);
            var counter1 = new AtomicCounter(0);
            var counter2 = new AtomicCounter(0);
            var actor1 = Sys.ActorOf(Props.Create(() => new BroadcastTarget(doneLatch, counter1)));
            var actor2 = Sys.ActorOf(Props.Create(() => new BroadcastTarget(doneLatch, counter2)));

            var routedActor = Sys.ActorOf(Props.Create<TestActor>().WithRouter(new BroadcastGroup(actor1.Path.ToString(), actor2.Path.ToString())));
            routedActor.Ask(new Broadcast(1));
            routedActor.Tell(new Broadcast("end"));

            doneLatch.Ready(TimeSpan.FromSeconds(1));

            counter1.Current.ShouldBe(1);
            counter2.Current.ShouldBe(1);
        }
    }
}
