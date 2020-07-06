using Moq;
using NUnit.Framework;
using Segment.Flush;
using Segment.Model;
using Segment.Request;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace Segment.Test.Flush
{
    [TestFixture]
    public class AsyncIntervalFlushHandlerTests
    {
        private const int MaxBatchSize = 512 * 1024;

        AsyncIntervalFlushHandler _handler;
        Mock<IRequestHandler> _mockRequestHandler;
        Func<Task> _requestHandlerBehavior;

        [SetUp]
        public void Init()
        {
            _requestHandlerBehavior = SingleTaskResponseBehavior(10);
            _mockRequestHandler = new Mock<IRequestHandler>();

            _mockRequestHandler.Setup(r => r.MakeRequest(It.IsAny<Batch>()))
                .Returns(() => _requestHandlerBehavior())
                .Verifiable();

            _handler = GetFlushHandler(100, 20, 2000);
            Logger.Handlers += LoggingHandler;
        }

        [TearDown]
        public void CleanUp()
        {
            _handler.Dispose();
            Logger.Handlers -= LoggingHandler;
        }

        [Test]
        public void FlushDoesNotMakeARequestWhenThereAreNotEvents()
        {
            _handler.Flush();

            _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(0));
        }

        [Test()]
        public void FlushMakesARequestWhenThereAreEvents()
        {
            _handler.Process(new Track(null, null, null, null)).GetAwaiter().GetResult();
            _handler.Flush();

            _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(1));
        }


        [Test]
        public void IntervalFlushIsTriggeredPeriodically()
        {
            var interval = 600;
            _handler = GetFlushHandler(100, 20, interval);
            Thread.Sleep(100);
            int trials = 5;

            for (int i = 0; i < trials; i++)
            {
                _handler.Process(new Track(null, null, null, null)).GetAwaiter().GetResult();
                _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(i));
                Thread.Sleep(interval);
            }

            _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(trials));
        }

        [Test]
        public void FlushSplitEventsInBatches()
        {
            var queueSize = 100;
            _handler = GetFlushHandler(queueSize, 20, 20000);
            Thread.Sleep(100);

            for (int i = 0; i < queueSize; i++)
            {
                _ = _handler.Process(new Track(null, null, null, null));
            }

            _handler.Flush();

            _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(5));
        }

        [Test]
        public void ProcessActionFlushWhenQueueIsFull()
        {
            var queueSize = 10;
            _handler = GetFlushHandler(queueSize, 20, 20000);
            Thread.Sleep(50);

            for (int i = 0; i < queueSize + 1; i++)
            {
                _ = _handler.Process(new Track(null, null, null, null));
            }

            _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(1));
        }


        [Test]
        public void FlushWaitsForPreviousFlushesTriggeredByInterval()
        {
            var time = 1500;
            _handler = GetFlushHandler(100, 20, 500);
            _requestHandlerBehavior = MultipleTaskResponseBehavior(time);

            DateTime start = DateTime.Now;
            _ = _handler.Process(new Track(null, null, null, null));

            Thread.Sleep(500);

            _handler.Flush();

            TimeSpan duration = DateTime.Now.Subtract(start);

            _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(1));
            //50 millisecons as error margin
            Assert.IsTrue(duration.CompareTo(TimeSpan.FromMilliseconds(time - 50)) >= 0);

        }

        [Test]
        public void IntervalFlushLimitConcurrentProcesses()
        {
            var time = 2000;
            _handler = GetFlushHandler(100, 20, 300);
            _requestHandlerBehavior = MultipleTaskResponseBehavior(time, 0, time);

            _ = _handler.Process(new Track(null, null, null, null));
            Thread.Sleep(400);

            for (int i = 0; i < 3; i++)
            {
                _handler.Process(new Track(null, null, null, null)).GetAwaiter().GetResult();
                _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(1));

                Thread.Sleep(300);
            }

            _handler.Flush();

            _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(2));

        }

        [Test]
        public void IntervalFlushTriggerTwoConcurrentProcesses()
        {

            var time = 2000;
            _handler = GetFlushHandler(100, 20, 300, 2);
            _requestHandlerBehavior = MultipleTaskResponseBehavior(time, 0, time);

            _ = _handler.Process(new Track(null, null, null, null));
            Thread.Sleep(400);

            for (int i = 0; i < 3; i++)
            {
                _handler.Process(new Track(null, null, null, null)).GetAwaiter().GetResult();
                //There is only the first process 
                _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(1));
            }

            Thread.Sleep(400);
            //The second process should be triggered
            _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(2));
            _handler.Flush();
            //Validating that flush doesn't triggered another process
            _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(2));
        }

        [Test]
        public void FlushCatchExceptions()
        {
            _mockRequestHandler.Setup(r => r.MakeRequest(It.IsAny<Batch>())).Throws<System.Exception>();
            
            _ = _handler.Process(new Track(null, null, null, null));

            _handler.Flush();

            _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), times: Times.Exactly(1));
        }

        [Test]
        public void IntervalFlushSendsBatchesThatAreSmallerThan512Kb()
        {
            _handler = GetFlushHandler(1000, 1000, 10000);

            Thread.Sleep(100);

            var actions = GetActions(999, GetEventName(30));

            foreach (var action in actions)
            {
                _ = _handler.Process(action);
            }

            _handler.Flush();

            _mockRequestHandler.Verify(r => r.MakeRequest(It.Is<Batch>(b => b.batch.Sum(a => a.Size) < MaxBatchSize)), times: Times.Exactly(1));


        }

        [Test]
        public void BatchMeetTheMaxNumberOfActions()
        {
            var flushAt = 20;
            var actionNumber = 45;
            _handler = GetFlushHandler(1000, flushAt, 10000);

            Thread.Sleep(100);

            var actions = GetActions(actionNumber, GetEventName(30));

            foreach (var action in actions)
            {
                _ = _handler.Process(action);
            }

            _handler.Flush();
            _mockRequestHandler.Verify(r => r.MakeRequest(It.IsAny<Batch>()), Times.Exactly(3));
            _mockRequestHandler.Verify(r => r.MakeRequest(It.Is<Batch>(b => b.batch.Count == flushAt)), Times.Exactly(2));
            _mockRequestHandler.Verify(r => r.MakeRequest(It.Is<Batch>(b => b.batch.Count == 5)), Times.Exactly(1));
        }
        private string GetEventName(int size)
        {
            return string.Join("", Enumerable.Range(0, size).Select(_ => "a").ToArray());
        }

        private List<BaseAction> GetActions(int actionsNumber, string eventName)
        {
            return new List<BaseAction>(Enumerable.Range(0, actionsNumber).Select(n => new Track("user", eventName, null, null)).ToArray());
        }


        private AsyncIntervalFlushHandler GetFlushHandler(int maxQueueSize, int maxBatchSize, int flushIntervalInMillis, int threads = 1)
        {
            return new AsyncIntervalFlushHandler(new SimpleBatchFactory("TestKey"), _mockRequestHandler.Object, maxQueueSize, maxBatchSize, flushIntervalInMillis, threads);
        }

        private Func<Task> SingleTaskResponseBehavior(int time)
        {
            return async () => Thread.Sleep(time);
        }

        private Func<Task> MultipleTaskResponseBehavior(params int[] time)
        {
            var response = new Queue<int>(time);
            return async () =>
            {
                var t = response.Count > 0 ? response.Dequeue() : 0;
                Thread.Sleep(t);
            };
        }
        static void LoggingHandler(Logger.Level level, string message, IDictionary<string, object> args)
        {
            if (args != null)
            {
                foreach (string key in args.Keys)
                {
                    message += String.Format(" {0}: {1},", "" + key, "" + args[key]);
                }
            }

            Console.WriteLine(String.Format("[FlushTests] [{0}] {1}", level, message));
        }

    }
}
