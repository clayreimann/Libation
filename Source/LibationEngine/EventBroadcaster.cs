using System.Collections.Concurrent;
using System.Threading.Channels;
using LibationEngine.Contracts;

namespace LibationEngine;

/// <summary>Fans events out to bounded per-subscriber channels; a slow subscriber loses its oldest events, never blocks the publisher.</summary>
public sealed class EventBroadcaster(int capacity = 256)
{
	private readonly ConcurrentDictionary<Subscription, byte> subscribers = new();

	public int SubscriberCount => subscribers.Count;

	public Subscription Subscribe()
	{
		var subscription = new Subscription(this, Channel.CreateBounded<EngineEvent>(new BoundedChannelOptions(capacity)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true,
		}));
		subscribers[subscription] = 0;
		return subscription;
	}

	public void Publish(EngineEvent evt)
	{
		foreach (var subscription in subscribers.Keys)
			subscription.Channel.Writer.TryWrite(evt);
	}

	public sealed class Subscription : IDisposable
	{
		private readonly EventBroadcaster owner;
		internal Channel<EngineEvent> Channel { get; }

		internal Subscription(EventBroadcaster owner, Channel<EngineEvent> channel)
		{
			this.owner = owner;
			Channel = channel;
		}

		public ChannelReader<EngineEvent> Reader => Channel.Reader;

		public void Dispose()
		{
			owner.subscribers.TryRemove(this, out _);
			Channel.Writer.TryComplete();
		}
	}
}
