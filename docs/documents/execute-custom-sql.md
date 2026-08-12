# Execute custom SQL in session

Use `QueueSqlCommand(string sql, params object[] parameterValues)` method to register and execute any custom/arbitrary SQL commands with the underlying unit of work, as part of the batched commands within `IDocumentSession`. 

`?` placeholders can be used to denote parameter values. Postgres [type casts `::`](https://www.postgresql.org/docs/15/sql-expressions.html#SQL-SYNTAX-TYPE-CASTS) can be applied to the parameter if needed. If the `?` character is not suitable as a placeholder because you need to use `?` in your sql query, you can change the placeholder by providing an alternative. Pass this in before the sql argument. 

<!-- snippet: sample_queuesqlcommand -->
<a id='snippet-sample_queuesqlcommand'></a>
```cs
theSession.QueueSqlCommand("insert into names (name) values ('Jeremy')");
theSession.QueueSqlCommand("insert into names (name) values ('Babu')");
theSession.Store(Target.Random());
theSession.QueueSqlCommand("insert into names (name) values ('Oskar')");
theSession.Store(Target.Random());
var json = "{ \"answer\": 42 }";
theSession.QueueSqlCommand("insert into data (raw_value) values (?::jsonb)", json);
// Use ^ as the parameter placeholder
theSession.QueueSqlCommand('^', "insert into data (raw_value) values (^::jsonb)", json);
```
<sup><a href='https://github.com/JasperFx/marten/blob/master/src/CoreTests/executing_arbitrary_sql_as_part_of_transaction.cs#L40-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_queuesqlcommand' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## The SQL must not return a result set

Marten batches every operation in the unit of work into a single command and reads their results
back in order. A queued SQL command is assumed to contribute nothing to read, so a statement that
does return rows leaves the batched reader one result set behind, and every operation queued after
it in the same session then reads the wrong one.

Nothing throws when this happens. The symptom appears somewhere else entirely, as a spurious
`ConcurrencyException` or a wrong version on an unrelated document
([#5210](https://github.com/JasperFx/marten/issues/5210)).

`insert`, `update` and `delete` are safe. The trap is calling a function for its side effect:

```cs
// Wrong. `select fn(...)` returns a one-row result set even when the function returns void.
session.QueueSqlCommand("select mark_progress(?, ?)", shard, 10L);

// Right. PERFORM inside a DO block runs the function and returns nothing.
session.QueueSqlCommand("do $$ begin perform mark_progress('shard-name', 10); end $$");
```

`DO` blocks do not accept parameters, so if you need parameter values, run the call on its own
connection outside the session instead of queueing it.

Debug builds of Marten assert on this at the end of each batch and name the offending operation
type, so a violation fails immediately in development rather than surfacing later as a confusing
concurrency error.
