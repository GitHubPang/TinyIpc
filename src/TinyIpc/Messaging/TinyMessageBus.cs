using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProtoBuf;
using TinyIpc.IO;

namespace TinyIpc.Messaging
{
	public class TinyMessageBus : IDisposable, ITinyMessageBus
	{
		private static long messageOverhead;

		private bool disposed;
		private readonly Guid instanceId = Guid.NewGuid();
		private readonly ConcurrentQueue<LogEntry> publishQueue = new ConcurrentQueue<LogEntry>();
		private readonly TimeSpan minMessageAge;
		private readonly object messageReaderLock = new object();
		private readonly object messagePublisherLock = new object();
		private readonly object publishTaskLock = new object();
		private readonly object readTaskLock = new object();
		private readonly ITinyMemoryMappedFile memoryMappedFile;
		private readonly bool disposeFile;

		private long lastEntryId = -1;
		private long messagesSent;
		private long messagesReceived;
		private Task[] publishTasks = new Task[0];
		private Task[] readTasks = new Task[0];
		private int waitingReaders;
		private int waitingPublishers;

		/// <summary>
		/// Called whenever a new message is received
		/// </summary>
		public event EventHandler<TinyMessageReceivedEventArgs> MessageReceived;

		public bool MessagesBeingProcessed => waitingReaders + waitingPublishers > 0;
		public long MessagesSent => messagesSent;
		public long MessagesReceived => messagesReceived;

		public static readonly TimeSpan DefaultMinMessageAge = TimeSpan.FromMilliseconds(500);

		static TinyMessageBus()
		{
			Serializer.PrepareSerializer<LogBook>();
			using (var memoryStream = new MemoryStream())
			{
				Serializer.Serialize(memoryStream, new LogEntry { Id = long.MaxValue, Instance = Guid.Empty, Timestamp = DateTime.UtcNow });
				messageOverhead = memoryStream.Length;
			}
		}

		public TinyMessageBus(string name)
			: this(new TinyMemoryMappedFile(name), true)
		{
		}

		public TinyMessageBus(string name, TimeSpan minMessageAge)
			: this(new TinyMemoryMappedFile(name), true, minMessageAge)
		{
		}

		public TinyMessageBus(ITinyMemoryMappedFile memoryMappedFile, bool disposeFile)
			: this(memoryMappedFile, disposeFile, DefaultMinMessageAge)
		{
		}

		public TinyMessageBus(ITinyMemoryMappedFile memoryMappedFile, bool disposeFile, TimeSpan minMessageAge)
		{
			this.minMessageAge = minMessageAge;
			this.memoryMappedFile = memoryMappedFile;
			this.disposeFile = disposeFile;

			memoryMappedFile.FileUpdated += HandleIncomingMessages;

			Warmup();
		}

		public void Dispose()
		{
			memoryMappedFile.FileUpdated -= HandleIncomingMessages;

			disposed = true;

			WaitAll();

			if (disposeFile && memoryMappedFile is TinyMemoryMappedFile)
			{
				(memoryMappedFile as TinyMemoryMappedFile).Dispose();
			}
		}

		/// <summary>
		/// Performs a synchonous warmup pass to make sure everything is jitted
		/// </summary>
		private void Warmup()
		{
			var lastEntry = DeserializeLogBook(memoryMappedFile.Read()).Entries.LastOrDefault();
			if (lastEntry != null)
			{
				lastEntryId = lastEntry.Id;
			}

			publishQueue.Enqueue(new LogEntry { Instance = instanceId, Message = new byte[0] });
			ProcessPublishQueue();
		}

		/// <summary>
		/// Resets MessagesSent and MessagesReceived counters
		/// </summary>
		public void ResetMetrics()
		{
			messagesSent = 0;
			messagesReceived = 0;
		}

		/// <summary>
		/// Publishes a message to the message bus as soon as possible in a background task
		/// </summary>
		/// <param name="message"></param>
		public void PublishAsync(byte[] message)
		{
			if (disposed)
				throw new ObjectDisposedException("Can not publish messages when diposed");

			if (message == null || message.Length == 0)
				throw new ArgumentException("Message can not be empty", nameof(message));

			publishQueue.Enqueue(new LogEntry { Instance = instanceId, Message = message });

			if (waitingPublishers > 0)
				return;

			StartPublishTask();
		}

		internal void WaitAll()
		{
			Task.WaitAll(publishTasks);
			Task.WaitAll(readTasks);
		}

		private void StartPublishTask()
		{
			if (disposed)
				return;

			lock (publishTaskLock)
			{
				publishTasks = publishTasks.Where(x => !x.IsCompleted)
					.Concat(new[] { Task.Run(() => ProcessPublishQueue()) })
					.ToArray();
			}
		}

		private void StartReadTask()
		{
			if (disposed)
				return;

			lock (readTaskLock)
			{
				readTasks = readTasks.Where(x => !x.IsCompleted)
					.Concat(new[] { Task.Run(() => ProcessIncomingMessages()) })
					.ToArray();
			}
		}

		private void ProcessPublishQueue()
		{
			Interlocked.Increment(ref waitingPublishers);

			lock (messagePublisherLock)
			{
				Interlocked.Decrement(ref waitingPublishers);

				if (publishQueue.Count == 0)
					return;

				while (waitingPublishers == 0 && publishQueue.Count > 0)
				{
					memoryMappedFile.ReadWrite(data => PublishMessages(data, TimeSpan.FromMilliseconds(100)));
				}
			}
		}

		private byte[] PublishMessages(byte[] data, TimeSpan timeout)
		{
			var logBook = DeserializeLogBook(data);
			logBook.TrimStaleEntries(DateTime.UtcNow - minMessageAge);
			var logSize = logBook.CalculateLogSize();

			// Start slot timer after deserializing log so deserialization doesn't starve the slot time
			var slotTimer = Stopwatch.StartNew();
			var batchTime = DateTime.UtcNow;

			// Try to exhaust the publish queue but don't keep a write lock forever
			while (publishQueue.Count > 0 && slotTimer.Elapsed < timeout)
			{
				// Check if the next message will fit in the log
				if (!publishQueue.TryPeek(out LogEntry entry) || logSize + messageOverhead + entry.Message.Length > memoryMappedFile.MaxFileSize)
					break;

				if (!publishQueue.TryDequeue(out entry))
					break;

				// Write the entry to the log
				entry.Id = logBook.NextId++;
				entry.Timestamp = batchTime;
				logBook.Entries.Add(entry);

				logSize += messageOverhead + entry.Message.Length;

				// Skip counting empty messages though, they are skipped on the receiving end anyway
				if (entry.Message == null || entry.Message.Length == 0)
					continue;

				Interlocked.Increment(ref messagesSent);
			}

			// Flush the updated log to the memory mapped file
			using (var memoryStream = new MemoryStream((int)logSize))
			{
				Serializer.Serialize(memoryStream, logBook);
				return memoryStream.ToArray();
			}
		}

		private void HandleIncomingMessages(object sender, EventArgs args)
		{
			if (waitingReaders > 0)
				return;

			StartReadTask();
		}

		internal void ProcessIncomingMessages()
		{
			Interlocked.Increment(ref waitingReaders);

			lock (messageReaderLock)
			{
				Interlocked.Decrement(ref waitingReaders);

				var logBook = DeserializeLogBook(memoryMappedFile.Read());

				foreach (var entry in logBook.Entries)
				{
					if (entry.Id <= lastEntryId)
						continue;

					lastEntryId = entry.Id;

					if (entry.Instance == instanceId || entry.Message == null || entry.Message.Length == 0)
						continue;

					MessageReceived?.Invoke(this, new TinyMessageReceivedEventArgs { Message = entry.Message });

					Interlocked.Increment(ref messagesReceived);
				}
			}
		}

		private static LogBook DeserializeLogBook(byte[] data)
		{
			if (data.Length == 0)
				return new LogBook();

			using (var memoryStream = new MemoryStream(data))
			{
				return Serializer.Deserialize<LogBook>(memoryStream);
			}
		}

		[ProtoContract]
		private class LogBook
		{
			[ProtoMember(1)]
			public long NextId { get; set; }

			[ProtoMember(2)]
			public List<LogEntry> Entries { get; set; } = new List<LogEntry>();

			public long CalculateLogSize()
			{
				return sizeof(long) + Entries.Select(l => messageOverhead + l.Message.Length).Sum();
			}

			public void TrimStaleEntries(DateTime cutoffPoint)
			{
				Entries = Entries.SkipWhile(entry => entry.Timestamp < cutoffPoint).ToList();
			}
		}

		[ProtoContract]
		private class LogEntry
		{
			[ProtoMember(1)]
			public long Id { get; set; }

			[ProtoMember(2)]
			public Guid Instance { get; set; }

			[ProtoMember(3)]
			public DateTime Timestamp { get; set; }

			[ProtoMember(4)]
			public byte[] Message { get; set; }
		}
	}
}
