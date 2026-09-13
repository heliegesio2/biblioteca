# CLAUDE.md

Contexto para agentes trabalhando neste repositório. Leia antes de escrever código.

## O que é

API REST em .NET 10 (LTS) para catálogo e empréstimos de biblioteca, entregue como
desafio técnico. O enunciado original está em `levantamento.pdf`, na raiz.

O que está sendo avaliado: **integridade dos dados, concorrência, rastreabilidade,
cache, testes e operação** em um serviço distribuído rodando em 2 a 11 réplicas.
Não é quantidade de endpoints nem sofisticação de arquitetura.

## Ordem de leitura

1. [README.md](README.md) — visão geral e as decisões principais (§8).
2. [docs/architecture.md](docs/architecture.md) — organização e pipeline CQRS.
3. [docs/concurrency.md](docs/concurrency.md) e
   [docs/idempotency.md](docs/idempotency.md) — o núcleo do desafio.
4. [docs/requirements-traceability.md](docs/requirements-traceability.md) — checklist
   requisito → implementação → teste.
5. [docs/implementation-plan.md](docs/implementation-plan.md) — fases e o que vem a seguir.

## Regras que não devem ser quebradas

Cada uma existe por um motivo documentado; se parecer que uma delas atrapalha, leia a
ADR correspondente antes de mudar.

1. **A decisão de disponibilidade é um `UPDATE` condicional atômico**, nunca
   ler-decidir-escrever. Nunca `book.AvailableCopies--` seguido de `SaveChanges()` no
   caminho de empréstimo. ([ADR-0003](docs/adr/0003-concorrencia-update-condicional.md))
2. **O cache nunca decide nada.** O caminho de escrita não lê cache.
   ([ADR-0007](docs/adr/0007-cache-invalidacao-pos-commit.md))
3. **Invalidação de cache só depois do commit**, via `ICacheInvalidationQueue`.
4. **Auditoria e idempotência gravam na mesma transação** que o efeito que descrevem ou
   protegem. ([ADR-0005](docs/adr/0005-idempotencia-no-postgres.md))
5. **Nada é apagado**: `DELETE /books/{id}` desativa; devolução/cancelamento mudam status.
6. **Migrations nunca rodam no start da API.**
   ([ADR-0008](docs/adr/0008-migrations-fora-do-startup.md))
7. **Nenhum segredo real versionado.** Só credenciais locais do Compose.
8. **`rowversion` não existe no PostgreSQL** — o token de concorrência é `xmin`, e só
   para edição de catálogo.
9. **Sem EF InMemory em teste.** Integração usa Testcontainers com Postgres e Redis reais.
   ([ADR-0006](docs/adr/0006-testes-com-testcontainers.md))
10. **`CancellationToken` propagado** em toda chamada assíncrona.

## Convenções de código

- C# com nullable habilitado e warnings como erro. `file`-scoped namespaces, `sealed` por
  padrão em classes que não são estendidas.
- Um caso de uso por arquivo: comando/query + validator + handler juntos.
- Handlers devolvem `Result<T>`; exceção só para o que é realmente excepcional. Rejeição
  de negócio não é exceção.
- Endpoints não contêm regra de negócio: traduzem HTTP → comando → HTTP.
- Domínio (`Features/*/Domain/`) não referencia EF Core, `HttpContext` nem `HybridCache`.
- Um slice não referencia o interno de outro. Exceção declarada: `Loans` escreve em
  `Book.AvailableCopies`.
- Nomes de banco em `snake_case`; instantes sempre `timestamptz`/UTC, obtidos de
  `TimeProvider` injetado (nunca `DateTime.UtcNow`).
- Logs com template e parâmetros nomeados, nunca interpolação.

## Comandos

```bash
docker compose up --build                       # API + Postgres + Redis + migrator
dotnet run --project src/Biblioteca.Api         # local (dependências no Compose)
dotnet test                                     # tudo; integração exige Docker
dotnet test tests/Biblioteca.UnitTests          # rápido, sem Docker
dotnet ef database update --project src/Biblioteca.Api
dotnet ef migrations add <Nome> --project src/Biblioteca.Api \
    --output-dir Infrastructure/Persistence/Migrations
helm template biblioteca deploy/helm/biblioteca # renderiza sem cluster
```

## Ao alterar algo

- Mudou uma decisão de engenharia? Atualize a ADR (ou escreva uma nova substituindo a
  anterior) **e** a seção correspondente do README.
- Mudou o contrato HTTP? Atualize [docs/api-contract.md](docs/api-contract.md).
- Adicionou ou removeu teste citado? Atualize
  [docs/requirements-traceability.md](docs/requirements-traceability.md).
- Documentação e código divergentes contam como bug: o README é a entrega tanto quanto o
  código.
