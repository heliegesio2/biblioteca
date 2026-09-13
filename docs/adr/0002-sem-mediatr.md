# ADR-0002 — Pipeline CQRS próprio, sem MediatR

**Status:** Aceita · **Data:** 2026-09-12

## Contexto

[ADR-0001](0001-cqrs-vertical-slices.md) estabelece CQRS com *cross-cutting concerns* em
um pipeline. A implementação de referência no ecossistema .NET é o MediatR
(`IRequest` + `IPipelineBehavior`). Duas considerações pesaram contra adotá-lo por
inércia:

1. **Licenciamento.** A partir da v13, MediatR passou a exigir licença comercial paga
   para uso em organizações; a v12 é a última versão gratuita. Introduzir uma decisão de
   licenciamento em um projeto desta dimensão é custo desproporcional.
2. **Proporção.** São 13 endpoints. A infraestrutura que o MediatR oferece — registro de
   handlers, despacho por tipo, behaviors encadeados — cabe em cerca de 80 linhas quando
   o requisito é exatamente esse e nada além.

## Decisão

Pipeline próprio em `Infrastructure/Cqrs/`:

```csharp
public interface ICommand<TResult>;
public interface IQuery<TResult>;

public interface ICommandHandler<TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> Handle(TCommand command, CancellationToken ct);
}

public interface IDispatcher
{
    Task<Result<TResult>> Send<TResult>(ICommand<TResult> command, CancellationToken ct);
    Task<Result<TResult>> Query<TResult>(IQuery<TResult> query, CancellationToken ct);
}
```

O `Dispatcher` resolve o handler pelo container. Os *cross-cutting concerns* são
**decorators registrados no DI** com `Scrutor.Decorate<,>`, na ordem definida em
[architecture.md](../architecture.md#o-pipeline-cqrs).

Decorators do DI, e não uma cadeia construída à mão, porque a ordem passa a ser um
registro declarativo e legível em um lugar só (`CqrsRegistration.cs`), e cada decorator
é instanciável isoladamente em teste.

## Consequências

**Positivas**

- Sem dependência externa de runtime no caminho crítico, e sem questão de licença.
- A ordem dos decorators é explícita e testável — e essa ordem é onde mora a corretude:
  idempotência precisa estar **dentro** da transação, invalidação de cache **fora** dela.
  Com behaviors de biblioteca, essa sutileza fica implícita na ordem de registro.
- `Result<T>` em vez de exceção para fluxo esperado: rejeição de negócio (`409`, `422`)
  não usa o custo nem o ruído de uma exceção.

**Negativas**

- ~80 linhas a manter, incluindo o tratamento de genéricos abertos no registro.
- Pessoas que chegam esperando `ISender`/`IRequest` precisam de uma leitura para se
  situar — o formato é deliberadamente familiar para reduzir esse custo.
- Sem os extras do MediatR (notificações, *streaming*), que este projeto não usa.

## Alternativas descartadas

| Alternativa | Por que não |
|---|---|
| MediatR v12 (última gratuita) | Fixar-se numa versão que não recebe mais evolução para economizar 80 linhas |
| MediatR v13+ | Licença comercial paga, desproporcional ao projeto |
| Wolverine / Brighter | Trazem mensageria, *outbox* e runtime próprio — muito maior do que o necessário |
| Sem pipeline: cada handler abre transação e invalida cache | É onde os bugs moram. Um handler que esquece de invalidar, ou que invalida antes do commit, quebra a coerência de forma silenciosa |
