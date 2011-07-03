﻿using System;
using System.Text;
using System.Threading;
using NUnit.Framework;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SharpTestsEx;
using log4net.Appender;
using log4net.Config;
using log4net.Layout;
using log4net.Repository;
using RabbitMQ.Client.Framing.v0_9_1;

namespace log4net.RabbitMQ.Tests
{
	public class rabbitmq_appender_spec
	{
		private static readonly ILog _TestLog = LogManager.GetLogger(typeof (rabbitmq_appender_spec));
		private ILoggerRepository _Rep;

		[SetUp]
		public void given_a_logger_and_listener()
		{

			_Rep = LogManager.CreateRepository(Guid.NewGuid().ToString());

			_Appender = new RabbitMQAppender { Layout = new PatternLayout() };
			_Appender.ActivateOptions();

			BasicConfigurator.Configure(_Rep, _Appender);
			BasicConfigurator.Configure(new ConsoleAppender() { Layout = new SimpleLayout() });
			_Log = LogManager.GetLogger(_Rep.Name, GetType());
			_Listener = SetUpRabbitListener();
		}

		private Tuple<IModel, IConnection> SetUpRabbitListener()
		{
			var connFac = new ConnectionFactory
			{
				HostName = _Appender.HostName,
				VirtualHost = "/",
				UserName = "guest",
				Password = "guest",
				Protocol = Protocols.DefaultProtocol,
			};

			var conn = connFac.CreateConnection();
			var model = conn.CreateModel();

			_TestLog.Debug("started rmq on test-side");

			return Tuple.Create(model, conn);
		}

		private ILog _Log;
		private Tuple<IModel, IConnection> _Listener;
		private RabbitMQAppender _Appender;
		private readonly string _Q = "log4net.RabbitMQ.Tests";

		[TearDown]
		public void then_finally_close_listener()
		{
			var conn = _Listener.Item2;
			conn.AutoClose = true;

			try
			{
				var model = _Listener.Item1;
				model.QueuePurge(_Q);
				model.QueueUnbind(_Q, _Appender.Exchange, "#", null);
				
				// only for this unit test, to keep initial test state same.
				model.ExchangeDelete(_Appender.Exchange);

				model.Close(Constants.ReplySuccess, "test done");
			}
			finally
			{
				conn.Dispose();
			}
		}

		[Test, MaxTime(10000)]
		public void when_debugging_message()
		{
			var msg = "My Message";

			var gotIt = new ManualResetEventSlim(false);
			BasicDeliverEventArgs result = null;
			GetMessage(gotIt, r => result = r);

			_Log.Debug(msg);
			
			_TestLog.Debug("waiting for message on test thread");
			gotIt.Wait();

			result.Should(" This applies to the result").Not.Be.Null();
			var message = Encoding.UTF8.GetString(result.Body);
			message.Should().Contain(msg);
		}

		[Test, MaxTime(10000)]
		public void when_logging_stacktrace()
		{
			var gotIt = new ManualResetEventSlim(false);
			BasicDeliverEventArgs result = null;
			GetMessage(gotIt, r => result = r);

			try
			{
				throw new ApplicationException("this is so wrong");
			}
			catch (ApplicationException ex)
			{
				_Log.Error("noo, something very wrong", ex);
			}

			_TestLog.Debug("waiting for message on test thread");
			gotIt.Wait();

			var message = Encoding.UTF8.GetString(result.Body);
			_TestLog.Debug("got message:" + message);

			message.Satisfy(x => x.Contains("noo, something very wrong")
			                     && x.Contains("this is so wrong")
								 && x.Contains("when_logging_stacktrace"));
		}

		private void GetMessage(ManualResetEventSlim gotIt, Action<BasicDeliverEventArgs> delivered)
		{
			var started = new ManualResetEventSlim(false);
			var t = new Thread(() =>
			{
				try
				{
					var consumer = StartListening();
					started.Set();
					_TestLog.Debug("waiting for dq");
					object res;
					consumer.Queue.Dequeue(5000, out res).Should("because we just enqueued something").Be.True();
					var result = (BasicDeliverEventArgs)res;
					delivered(result);
					gotIt.Set();
				}
				catch(Exception e)
				{
					_TestLog.Error("listener error", e);
				}
				finally
				{
					gotIt.Set();
				}
			});
			t.Name = "Test AMQP Consumer Thread";
			t.Start();
			started.Wait();
		}

		private QueueingBasicConsumer StartListening()
		{
			var model = _Listener.Item1;
			var queue = new QueueingBasicConsumer(model);
			model.QueueDeclare(_Q, false, true, true, null);
			model.QueueBind(_Q, _Appender.Exchange, "#", null);
			model.BasicConsume(_Q, true, queue);

			return queue;
		}
	}
}