# ADR-0006 — Integração com PostgreSQL e Redis reais (Testcontainers)

**Status:** Aceita · **Data:** 2026-09-12

## Contexto

O enunciado exige teste de integração com "PostgreSQL real ou containerizado" e um teste
concorrente para o último exemplar disponível.

A corretude deste sistema não está no código C#: está no comportamento do banco — a
reavaliação do `WHERE` em `READ COMMITTED`, o bloqueio do segundo `INSERT` em índice
único, o `CHECK` de estoque, o índice único parcial. Um duplo em memória não reproduz
nenhuma dessas coisas.

## Decisão

**Testcontainers** subindo `postgres:17-alpine` e `redis:7-alpine` por collection de
testes, com `WebApplicationFactory<Program>` apontada para eles. Limpeza de dados entre
testes com **Respawn** (truncate), não recriação do banco.

O provider **InMemory do EF Core não é usado em lugar nenhum**.

## Consequências

**Positivas**

- O teste de concorrência testa o que precisa ser testado. Contra InMemory, ele passaria
  **inclusive com o código errado** — o pior tipo de teste que existe.
- Constraints, índices parciais, `citext`, `pg_trgm` e as próprias migrations são
  exercitados: o esquema é validado a cada execução.
- O cenário "Redis fora do ar" é testável de verdade, parando o container.
- Reprodutível em qualquer máquina e em CI sem serviço externo provisionado.

**Negativas**

- Exige Docker para rodar a suíte completa. Mitigado: `Biblioteca.UnitTests` roda sem
  Docker e cobre as regras de negócio puras.
- Mais lento (~40s no total, contra menos de 1s dos unitários). Mitigado compartilhando
  containers por collection e usando truncate em vez de recriar.
- Os testes que exigem paralelismo real precisam de collection isolada, para não sofrerem
  interferência de dados de outros testes.

## Alternativas descartadas

| Alternativa | Por que não |
|---|---|
| EF Core InMemory | Sem transações reais, sem constraints, sem concorrência. Testaria o mock, não o sistema |
| SQLite em memória | Semântica de transação e concorrência diferente do PostgreSQL; nem `xmin`, nem índice parcial, nem `citext` |
| Mocks de repositório | Verificariam se os mocks foram configurados corretamente. O `UPDATE` condicional — o núcleo da solução — sequer apareceria |
| PostgreSQL compartilhado em CI | Estado entre execuções, testes paralelos interferindo entre si, e um serviço a provisionar |

## Detalhe que fez diferença no desenho do teste

O teste do último exemplar usa **20 usuários distintos**, não um só. Com o mesmo usuário,
quem rejeitaria as 19 tentativas seria o índice único parcial
(`UNIQUE (book_id, user_id) WHERE status='Active'`) — o teste passaria sem que o `UPDATE`
condicional estivesse correto, dando falsa confiança exatamente no ponto mais crítico.

Estratégia completa: [testing.md](../testing.md).
