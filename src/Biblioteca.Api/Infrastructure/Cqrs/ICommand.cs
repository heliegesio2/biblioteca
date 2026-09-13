namespace Biblioteca.Api.Infrastructure.Cqrs;

/// <summary>
/// Marca um comando (escrita): passa por Metrics → Validation → CacheInvalidation →
/// Transaction → Idempotency → Handler (docs/architecture.md#o-pipeline-cqrs).
/// </summary>
public interface ICommand<TResult>;

/// <summary>
/// Marca uma consulta (leitura): passa por Metrics → Validation → Handler. Nunca abre
/// transação nem grava auditoria.
/// </summary>
public interface IQuery<TResult>;
