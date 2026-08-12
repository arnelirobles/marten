#nullable enable
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core.Exceptions;
using Marten.Events.Daemon.Progress;
using Marten.Internal.Operations;
using Marten.Services;
using Npgsql;
using Weasel.Postgresql;

namespace Marten.Internal.Sessions;

public class OperationPage
{
    private IMartenSession _session;
    private readonly BatchBuilder _builder;
    private readonly List<Weasel.Storage.IStorageOperation> _operations = new();

    public OperationPage(IMartenSession session)
    {
        _session = session;
        _builder = new BatchBuilder();
    }

    public OperationPage(IMartenSession session, IReadOnlyList<Weasel.Storage.IStorageOperation> operations) : this(session)
    {
        _operations.AddRange(operations);
        foreach (var operation in operations)
        {
            _builder.StartNewCommand();
            operation.ConfigureCommand(_builder, _session);
        }

        Count = _operations.Count;
    }

    public int Count { get; private set; }
    public IReadOnlyList<Weasel.Storage.IStorageOperation> Operations => _operations;

    public void Append(Weasel.Storage.IStorageOperation operation)
    {
        if (_session == null) return;

        Count++;
        _builder.StartNewCommand();
        operation.ConfigureCommand(
            _builder,
            _session ?? throw new InvalidOperationException("Session already released!")
        );
        _builder.Append(";");
        _operations.Add(operation);
    }

    public NpgsqlBatch Compile()
    {
        return _builder.Compile();
    }

    public void ReleaseSession()
    {
        _session = null;
    }

    public async Task ApplyCallbacksAsync(DbDataReader reader,
        IList<Exception> exceptions,
        CancellationToken token)
    {
        // 9.0 (#4375): indexed loop avoids the SkipIterator + List enumerator allocations
        // the old `_operations.First()` + `_operations.Skip(1)` pattern triggered per page.
        var first = _operations[0];

        if (first is not NoDataReturnedCall)
        {
            await first.PostprocessAsync(reader, exceptions, token).ConfigureAwait(false);
            try
            {
                await reader.NextResultAsync(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (first is IExceptionTransform t && t.TryTransform(e, out var transformed))
                {
                    throw transformed;
                }

                throw;
            }
        }
        else if (first is AssertsOnCallback)
        {
            await first.PostprocessAsync(reader, exceptions, token).ConfigureAwait(false);
        }

        for (var i = 1; i < _operations.Count; i++)
        {
            var operation = _operations[i];
            if (operation is NoDataReturnedCall)
            {
                continue;
            }

            await operation.PostprocessAsync(reader, exceptions, token).ConfigureAwait(false);
            try
            {
                await reader.NextResultAsync(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (operation is IExceptionTransform t && t.TryTransform(e, out var transformed))
                {
                    throw transformed;
                }

                throw;
            }
        }

#if DEBUG
        assertNoUnconsumedResultSet(reader);
#endif
    }

#if DEBUG
    /// <summary>
    ///     Guards the invariant behind <see cref="NoDataReturnedCall" />: the marker tells this method not
    ///     to advance the reader past that operation, which is only sound while its SQL genuinely produces
    ///     no result set. Postgres surfaces no result set for a statement that returns no rows, so a page
    ///     whose markers all told the truth leaves nothing unread once the loop above has run.
    /// </summary>
    /// <remarks>
    ///     An operation that declares the marker while returning rows does not break itself. It shifts every
    ///     later operation in the page onto the wrong result set, so the failure surfaces as a spurious
    ///     ConcurrencyException or a wrong version on an unrelated aggregate, nowhere near the cause. That is
    ///     what made #5210 expensive to find, and this turns the whole class into an immediate, named failure
    ///     for anyone running the suite locally.
    /// </remarks>
    private void assertNoUnconsumedResultSet(DbDataReader reader)
    {
        // FieldCount is the whole test. Npgsql reports it as zero once the reader is exhausted
        // and zero for a batch that produced no result sets, and greater than zero whenever the
        // reader is still positioned on one. Asking NextResultAsync instead would miss the case
        // of a single unconsumed result set, because "is there another one after this" is false
        // when the one you are sitting on is the only one.
        if (reader.FieldCount == 0)
        {
            return;
        }

        var suspects = _operations
            .OfType<NoDataReturnedCall>()
            .Select(x => x.GetType().Name)
            .ToArray();

        var named = suspects.Length == 0
            ? "no operation in the page declares the marker, so the extra result set came from somewhere else"
            : string.Join(", ", suspects);

        throw new InvalidOperationException(
            "A batched operation page left a result set unread. An operation marked NoDataReturnedCall emitted "
            + "SQL that returns a result set, which puts every operation after it in the page on the wrong data. "
            + $"Operations declaring the marker in this page: {named}. See marten#5210 for the failure this causes.");
    }
#endif
}
