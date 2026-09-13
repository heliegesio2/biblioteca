# ADR-0003 — Atualização condicional atômica para o último exemplar

**Status:** Aceita · **Data:** 2026-09-12

## Contexto

Requisito central do desafio: com um exemplar disponível, duas requisições simultâneas
de empréstimo devem produzir exatamente um sucesso, uma rejeição clara de regra de
negócio e nenhum estado inconsistente — com a API em múltiplas réplicas.

O padrão ingênuo (ler `available_copies`, testar em memória, gravar o decremento) tem uma
janela entre leitura e escrita. `lock` no processo não resolve, porque os processos são
diferentes. O enunciado ainda proíbe explicitamente `rowversion`, por ser recurso de SQL
Server.

## Decisão

**A leitura, a decisão e a escrita são a mesma instrução SQL**, dentro de uma transação
`READ COMMITTED`:

```sql
UPDATE books
   SET available_copies = available_copies - 1
 WHERE id = @bookId AND is_active AND available_copies > 0;
```

`0 linhas afetadas` → `409 book-unavailable`. `1 linha` → segue o `INSERT` do empréstimo,
o evento de auditoria e a finalização da idempotência, tudo na mesma transação.

Funciona porque, em `READ COMMITTED`, o PostgreSQL bloqueia a segunda transação que tenta
atualizar a mesma linha e, após o commit da primeira, **reavalia a cláusula `WHERE` sobre
a versão nova** da linha (`EvalPlanQual`). Com `available_copies = 0`, a condição falha e
nada é atualizado.

Reforço no banco, independente da aplicação:
`CHECK (available_copies >= 0 AND available_copies <= total_copies)` e
`UNIQUE (book_id, user_id) WHERE status = 'Active'`.

Complemento: **`xmin` como token de concorrência otimista para edição de catálogo**
(`PATCH /books/{id}` com `If-Match` → `412`). Pessimista onde o conflito é disputa por
recurso escasso; otimista onde é divergência de intenção.

## Consequências

**Positivas**

- Não existe janela entre decidir e escrever. A corretude não depende de a aplicação
  estar certa; depende de uma instrução atômica e de constraints.
- Nenhum `abort`/retry no caminho feliz: `READ COMMITTED` reavalia em vez de abortar.
- Rejeição vira erro de negócio explícito (`book-unavailable`), não erro genérico de
  banco — exatamente o que o enunciado pede.

**Negativas**

- A decisão fica em SQL (`ExecuteUpdateAsync`), fora do modelo de objetos: o método
  `Book.Borrow()` existe e é testado unitariamente, mas não é ele que garante a
  invariante sob concorrência. Essa duplicidade é intencional e está documentada.
- Linha quente: um título muito disputado serializa numa única linha de `books`.
  Irrelevante no volume do desafio; em escala, viraria *bucketing* ou exemplares
  individuais.
- Depende de comportamento específico do PostgreSQL em `READ COMMITTED`. É por isso que o
  teste de concorrência roda contra PostgreSQL real ([ADR-0006](0006-testes-com-testcontainers.md)).

## Alternativas descartadas

| Alternativa | Por que não |
|---|---|
| `SELECT … FOR UPDATE` | Correto, mas segura o lock pelo round-trip inteiro da aplicação em vez de uma instrução. Plano B natural se a decisão passar a exigir várias leituras consistentes |
| `xmin` no livro para o empréstimo | Falso conflito: com 50 exemplares, duas retiradas simultâneas são ambas legítimas, mas a segunda falharia. Retry em cascata em linha quente |
| `SERIALIZABLE` | Aborts `40001` e retry em toda a aplicação para um problema que uma instrução resolve |
| Lock distribuído no Redis | Colocaria corretude num componente que queremos descartável (expiração de lock, *clock skew*, *fencing*) |
| Fila serializando empréstimos | Trocaria `201`/`409` síncronos por processamento assíncrono, mudando o contrato |
| `rowversion` | Não existe no PostgreSQL; vetado pelo enunciado |

Detalhamento, diagramas e o teste: [concurrency.md](../concurrency.md).
