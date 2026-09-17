# Explicit transaction thread ownership

`BeginTrans`, all operations in the transaction, and `Commit` or `Rollback` must
run synchronously on the same managed thread. Do not put `await` inside that block.
A nested `BeginTrans` returns false and joins the existing thread transaction;
it does not create an independent transaction or savepoint.

When the calling thread has no transaction while another thread has an active
explicit transaction, `Commit` and `Rollback` throw a descriptive `LiteException`.
They leave the owner's transaction untouched. With no transaction anywhere,
these methods continue to return false.

This guard cannot distinguish two logical tasks that reuse a thread which already
owns a transaction. It does not make explicit transactions async-safe. Keep the
whole block synchronous, use a dedicated thread when necessary, or rely on the
per-operation automatic transactions. An explicit transaction-handle API and
non-thread-affine write locks remain separate future work.

In shared mode, if the owner thread exits and abandons its named mutex, the next
operation on that instance rejects the abandoned transaction and discards its uncommitted state.
The database can then be disposed normally. Foreign completion while the owner
is still alive preserves that owner's transaction.
