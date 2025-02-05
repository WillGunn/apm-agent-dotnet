﻿// Licensed to Elasticsearch B.V under
// one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Elastic.Apm.Api;
using Elastic.Apm.Logging;
using Elastic.Apm.Metrics;
using Elastic.Apm.Model;
using Elastic.Apm.Report;

namespace Elastic.Apm.Tests.Mocks
{
	internal class MockPayloadSender : IPayloadSender
	{
		private readonly List<IError> _errors = new List<IError>();
		private readonly object _lock = new object();
		private readonly List<IMetricSet> _metrics = new List<IMetricSet>();
		private readonly List<Func<ISpan, ISpan>> _spanFilters = new List<Func<ISpan, ISpan>>();
		private readonly List<ISpan> _spans = new List<ISpan>();
		private readonly List<Func<ITransaction, ITransaction>> _transactionFilters = new List<Func<ITransaction, ITransaction>>();
		private readonly List<ITransaction> _transactions = new List<ITransaction>();

		public MockPayloadSender(IApmLogger logger = null)
		{
			_waitHandles = new[]
			{
				new AutoResetEvent(false),
				new AutoResetEvent(false),
				new AutoResetEvent(false),
				new AutoResetEvent(false)
			};

			_transactionWaitHandle = _waitHandles[0];
			_spanWaitHandle = _waitHandles[1];
			_errorWaitHandle = _waitHandles[2];
			_metricSetWaitHandle = _waitHandles[3];

			PayloadSenderV2.SetUpFilters(_transactionFilters, _spanFilters, MockApmServerInfo.Version710, logger ?? new NoopLogger());
		}

		private readonly AutoResetEvent _transactionWaitHandle;
		private readonly AutoResetEvent _spanWaitHandle;
		private readonly AutoResetEvent _errorWaitHandle;
		private readonly AutoResetEvent _metricSetWaitHandle;
		private readonly AutoResetEvent[] _waitHandles;

		/// <summary>
		/// Waits for any events to be queued
		/// </summary>
		/// <param name="timeout">Optional timeout to wait</param>
		public void WaitForAny(TimeSpan? timeout = null)
		{
			if (timeout is null)
				WaitHandle.WaitAny(_waitHandles);
			else
				WaitHandle.WaitAny(_waitHandles, timeout.Value);
		}

		/// <summary>
		/// Waits for transactions to be queued
		/// </summary>
		/// <param name="timeout">Optional timeout to wait</param>
		/// <param name="count"></param>
		/// <returns><c>true</c> if the event was signalled, <c>false</c> otherwise.</returns>
		public void WaitForTransactions(TimeSpan? timeout = null, int? count = null)
		{
			if (count != null)
			{
				if (timeout is null)
				{
					while (_transactions.Count < count)
						_transactionWaitHandle.WaitOne();

					return;
				}

				var stopWatch = Stopwatch.StartNew();
				while (_transactions.Count < count)
				{
					var elapsedMilliseconds = Convert.ToInt32(timeout.Value.TotalMilliseconds - stopWatch.ElapsedMilliseconds);
					var signalled = _transactionWaitHandle.WaitOne(elapsedMilliseconds);

					if (!signalled) return;
				}

				return;
			}

			if (timeout is null)
				_transactionWaitHandle.WaitOne();
			else
				_transactionWaitHandle.WaitOne(timeout.Value);
		}

		/// <summary>
		/// Waits for spans to be queued
		/// </summary>
		/// <param name="timeout">Optional timeout to wait</param>
		/// <returns><c>true</c> if the event was signalled, <c>false</c> otherwise.</returns>
		public void WaitForSpans(TimeSpan? timeout = null, int? count = null)
		{
			if (count != null)
			{
				if (timeout is null)
				{
					while (_spans.Count < count)
						_spanWaitHandle.WaitOne();

					return;
				}

				var stopWatch = Stopwatch.StartNew();
				while (_spans.Count < count)
				{
					var elapsedMilliseconds = Convert.ToInt32(timeout.Value.TotalMilliseconds - stopWatch.ElapsedMilliseconds);
					var signalled = _spanWaitHandle.WaitOne(elapsedMilliseconds);

					if (!signalled)
						return;
				}

				return;
			}

			if (timeout is null)
				_spanWaitHandle.WaitOne();
			else
				_spanWaitHandle.WaitOne(timeout.Value);
		}

		/// <summary>
		/// Waits for errors to be queued
		/// </summary>
		/// <param name="timeout">Optional timeout to wait</param>
		/// <returns><c>true</c> if the event was signalled, <c>false</c> otherwise.</returns>
		public void WaitForErrors(TimeSpan? timeout = null)
		{
			if (timeout is null)
				_errorWaitHandle.WaitOne();
			else
				_errorWaitHandle.WaitOne(timeout.Value);
		}

		/// <summary>
		/// Waits for metrics to be queued
		/// </summary>
		/// <param name="timeout">Optional timeout to wait</param>
		/// <returns><c>true</c> if the event was signalled, <c>false</c> otherwise.</returns>
		public void WaitForMetrics(TimeSpan? timeout = null)
		{
			if (timeout is null)
				_metricSetWaitHandle.WaitOne();
			else
				_metricSetWaitHandle.WaitOne(timeout.Value);
		}

		/// <summary>
		/// Sets transaction wait handle to signalled, allowing threads to proceed.
		/// Can be called when making an assertion on the absence of a transaction where
		/// the order of execution is known, to prevent waiting for a given timeout.
		/// </summary>
		public void SignalEndTransactions() => _transactionWaitHandle.Set();

		/// <summary>
		/// Sets spans wait handle to signalled, allowing threads to proceed.
		/// Can be called when making an assertion on the absence of a span where
		/// the order of execution is known, to prevent waiting for a given timeout.
		/// </summary>
		public void SignalEndSpans() => _spanWaitHandle.Set();

		public IReadOnlyList<IError> Errors => CreateImmutableSnapshot(_errors);

		public Error FirstError => _errors.FirstOrDefault() as Error;
		public MetricSet FirstMetric => _metrics.FirstOrDefault() as MetricSet;

		/// <summary>
		/// The 1. Span on the 1. Transaction
		/// </summary>
		public Span FirstSpan => _spans.FirstOrDefault() as Span;

		public Transaction FirstTransaction =>
			Transactions.FirstOrDefault() as Transaction;

		public IReadOnlyList<IMetricSet> Metrics => CreateImmutableSnapshot(_metrics);

		public IReadOnlyList<ISpan> Spans => CreateImmutableSnapshot(_spans);

		public IReadOnlyList<ITransaction> Transactions => CreateImmutableSnapshot(_transactions);

		public Span[] SpansOnFirstTransaction =>
			_spans.Where(n => n.TransactionId == Transactions.First().Id).Select(n => n as Span).ToArray();

		public void QueueError(IError error)
		{
			_errors.Add(error);
			_errorWaitHandle.Set();
		}

		public virtual void QueueTransaction(ITransaction transaction)
		{
			transaction = _transactionFilters.Aggregate(transaction, (current, filter) => filter(current));
			_transactions.Add(transaction);
			_transactionWaitHandle.Set();
		}

		public void QueueSpan(ISpan span)
		{
			lock (_lock)
			{
				span = _spanFilters.Aggregate(span, (current, filter) => filter(current));
				_spans.Add(span);
				_spanWaitHandle.Set();
			}
		}

		public void QueueMetrics(IMetricSet metricSet)
		{
			_metrics.Add(metricSet);
			_metricSetWaitHandle.Set();
		}

		public void Clear()
		{
			_spans.Clear();
			_errors.Clear();
			_transactions.Clear();
			_metrics.Clear();
		}

		private static IReadOnlyList<T> CreateImmutableSnapshot<T>(IEnumerable<T> source) => new List<T>(source);
	}
}
