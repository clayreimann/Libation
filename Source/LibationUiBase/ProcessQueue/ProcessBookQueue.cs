using LibationEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace LibationUiBase.ProcessQueue;

/// <summary>The engine's job queue seen as view models, for the lists and counters that bind to it.</summary>
public sealed class ProcessBookQueue : IReadOnlyCollection<ProcessBookViewModel>, IList, INotifyCollectionChanged
{
	private readonly TrackedQueue<LiberationJob> _jobs;
	private readonly ConditionalWeakTable<LiberationJob, ProcessBookViewModel> _viewModels = new();

	public event EventHandler<int>? CompletedCountChanged;
	public event EventHandler<int>? QueuedCountChanged;
	public event NotifyCollectionChangedEventHandler? CollectionChanged;

	internal ProcessBookQueue(TrackedQueue<LiberationJob> jobs)
	{
		_jobs = jobs;
		_jobs.CompletedCountChanged += (_, count) => CompletedCountChanged?.Invoke(this, count);
		_jobs.QueuedCountChanged += (_, count) => QueuedCountChanged?.Invoke(this, count);
		_jobs.CollectionChanged += Jobs_CollectionChanged;
	}

	public ISynchronizeInvoke? NotificationInvoker { get => _jobs.NotificationInvoker; set => _jobs.NotificationInvoker = value; }

	public IReadOnlyList<ProcessBookViewModel> Completed => new MappedList(this);

	public ProcessBookViewModel this[int index] => For(_jobs[index]);
	public int Count => _jobs.Count;
	public int IndexOf(ProcessBookViewModel item) => _jobs.IndexOf(item.Job);

	internal ProcessBookViewModel For(LiberationJob job)
		=> _viewModels.TryGetValue(job, out var vm) ? vm : throw new InvalidOperationException("Job is not in this queue.");

	public void Enqueue(IList<ProcessBookViewModel> items)
	{
		foreach (var item in items)
			_viewModels.AddOrUpdate(item.Job, item);
		_jobs.Enqueue(items.Select(i => i.Job).ToList());
	}

	public bool TryDequeueNext([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ProcessBookViewModel item)
	{
		if (_jobs.TryDequeueNext(out var job))
		{
			item = For(job);
			return true;
		}
		item = null;
		return false;
	}

	public void MarkCompleted(ProcessBookViewModel item) => _jobs.MarkCompleted(item.Job);
	public bool RemoveQueued(ProcessBookViewModel item) => _jobs.RemoveQueued(item.Job);
	public bool RemoveCompleted(ProcessBookViewModel item) => _jobs.RemoveCompleted(item.Job);
	public void RemoveActive(ProcessBookViewModel item) => _jobs.RemoveActive(item.Job);
	public void MoveQueuePosition(ProcessBookViewModel item, QueuePosition position) => _jobs.MoveQueuePosition(item.Job, position);
	public void ClearCompleted() => _jobs.ClearCompleted();
	public void ClearQueue() => _jobs.ClearQueue();
	public IReadOnlyList<ProcessBookViewModel> GetActive() => _jobs.GetActive().Select(For).ToList();

	public IEnumerator<ProcessBookViewModel> GetEnumerator() => _jobs.GetAllItems().Select(For).GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	private void Jobs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		var handler = CollectionChanged;
		if (handler is null)
			return;

		var newItems = Map(e.NewItems);
		var oldItems = Map(e.OldItems);

		handler(this, e.Action switch
		{
			NotifyCollectionChangedAction.Add => new(e.Action, newItems, e.NewStartingIndex),
			NotifyCollectionChangedAction.Remove => new(e.Action, oldItems, e.OldStartingIndex),
			NotifyCollectionChangedAction.Move => new(e.Action, newItems, e.NewStartingIndex, e.OldStartingIndex),
			_ => new(NotifyCollectionChangedAction.Reset)
		});
	}

	private List<ProcessBookViewModel>? Map(IList? items)
		=> items?.Cast<LiberationJob>().Select(For).ToList();

	private sealed class MappedList(ProcessBookQueue owner) : IReadOnlyList<ProcessBookViewModel>
	{
		public int Count => owner._jobs.Completed.Count;
		public ProcessBookViewModel this[int index] => owner.For(owner._jobs.Completed[index]);
		public IEnumerator<ProcessBookViewModel> GetEnumerator() => owner._jobs.Completed.ToArray().Select(owner.For).GetEnumerator();
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	#region IList
	object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
	public bool IsReadOnly => true;
	public bool IsFixedSize => false;
	public bool IsSynchronized => false;
	public object SyncRoot => this;
	public int IndexOf(object? value) => value is ProcessBookViewModel vm ? IndexOf(vm) : -1;
	public bool Contains(object? value) => IndexOf(value) >= 0;
	public int Add(object? value) => throw new NotSupportedException();
	public void Clear() => throw new NotSupportedException();
	public void Insert(int index, object? value) => throw new NotSupportedException();
	public void Remove(object? value) => throw new NotSupportedException();
	public void RemoveAt(int index) => throw new NotSupportedException();
	public void CopyTo(Array array, int index) => throw new NotSupportedException();
	#endregion
}
