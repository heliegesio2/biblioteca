# Architecture Decision Records

Decisões estruturais do projeto, com o contexto que as motivou e as alternativas
descartadas. Uma ADR não é atualizada quando a decisão muda: ela é marcada como
*substituída* e uma nova entra no lugar, preservando o raciocínio original.

Formato: contexto → decisão → consequências → alternativas descartadas.

| # | Decisão | Status |
|---|---|---|
| [0001](0001-cqrs-vertical-slices.md) | CQRS dentro de vertical slices, em um projeto | Aceita |
| [0002](0002-sem-mediatr.md) | Pipeline CQRS próprio, sem MediatR | Aceita |
| [0003](0003-concorrencia-update-condicional.md) | Atualização condicional atômica para o último exemplar | Aceita |
| [0004](0004-exemplares-como-contagem.md) | Exemplares como contagem, não como entidades | Aceita |
| [0005](0005-idempotencia-no-postgres.md) | Chave de idempotência no PostgreSQL, na mesma transação | Aceita |
| [0006](0006-testes-com-testcontainers.md) | Integração com PostgreSQL e Redis reais | Aceita |
| [0007](0007-cache-invalidacao-pos-commit.md) | Invalidação explícita de cache após o commit | Aceita |
| [0008](0008-migrations-fora-do-startup.md) | Migrations fora do start da aplicação | Aceita |
