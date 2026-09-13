# Cache

Requisito: usar Redis em pelo menos uma consulta de leitura e invalidar/atualizar de
modo coerente quando empréstimo, devolução ou alteração de quantidade afetar o dado —
mantendo o PostgreSQL como fonte de verdade para a **decisão** de disponibilidade.

## Regra que governa tudo

> **O cache serve leitura. O cache nunca decide.**

Um valor velho no cache pode fazer um cliente ver "1 disponível" e receber `409` ao
tentar o empréstimo. Isso é aceitável e esperado. O que **não** pode acontecer é um
empréstimo ser criado porque o cache disse que havia exemplar — e não pode, porque o
caminho de escrita não lê o cache em momento algum: a decisão é o `UPDATE` condicional
no PostgreSQL ([concurrency.md](concurrency.md)).

Consequência prática: perder o Redis inteiro degrada latência, nunca corretude.

## O que é cacheado

| Chave | Conteúdo | TTL | Por quê |
|---|---|---|---|
| `book:v1:{id}` | `BookResponse` de `GET /books/{id}` | 300s (`Cache:BookTtlSeconds`) | Dados de catálogo mudam pouco e são lidos muito |
| `book:v1:{id}:availability` | `AvailabilityResponse` | 30s (`Cache:AvailabilityTtlSeconds`) | A consulta mais chamada; muda a cada empréstimo, daí o TTL curto |

O sufixo `v1` na chave é o **versionamento do formato serializado**: se o DTO mudar, a
versão sobe e as entradas antigas são simplesmente ignoradas, em vez de causar erro de
desserialização durante um deploy em que réplicas novas e antigas convivem.

## O que **não** é cacheado, e por quê

| Leitura | Motivo |
|---|---|
| `GET /books` (listagem/busca) | Espaço de chaves ilimitado: `search` × `availableOnly` × `cursor` × `limit`. A taxa de acerto seria baixa e a invalidação, imprecisa — qualquer alteração em qualquer livro deveria invalidar todas as combinações. O pior dos dois mundos: consome memória e ainda serve dado velho. |
| `GET /books/{id}/history`, `GET /users/{id}/loans` | Dados por usuário, com baixa repetição de leitura e alta sensibilidade a estar desatualizado (o usuário acabou de devolver e quer ver o resultado). |
| `GET /audit-events` | Trilha de auditoria precisa refletir o banco exatamente. Cachear trilha é como cachear um extrato bancário. |

Cachear menos, e cachear o que tem chave estável e leitura repetida, é a decisão —
não cachear tudo o que é `GET`.

## Implementação: `HybridCache`

`Microsoft.Extensions.Caching.Hybrid` com Redis como L2:

```csharp
builder.Services.AddStackExchangeRedisCache(o =>
    o.Configuration = builder.Configuration.GetConnectionString("Redis"));

builder.Services.AddHybridCache(o =>
{
    o.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration      = TimeSpan.FromSeconds(300),  // L2, Redis
        LocalCacheExpiration = TimeSpan.FromSeconds(5) // L1, memória do processo
    };
});
```

Por que `HybridCache` e não `IDistributedCache` direto:

- **Proteção contra *stampede***: requisições simultâneas para a mesma chave em miss
  resultam em **uma** consulta ao banco, não N. Na chave mais quente do sistema
  (`availability` de um lançamento), isso é a diferença entre um pico e um incidente.
- **L1 + L2 de graça**: a camada em memória absorve rajadas sem round-trip ao Redis.
- **Falha do L2 degrada para o banco**, em vez de propagar exceção.

### O L1 e a janela de inconsistência entre réplicas

O cache L1 vive na memória de cada pod, e a invalidação explícita (abaixo) remove a
entrada do Redis e do L1 **daquele pod**. Os outros pods continuariam servindo a cópia
local até ela expirar.

Por isso o `LocalCacheExpiration` é curto e explícito (5s): a janela em que uma réplica
pode servir disponibilidade velha é conhecida e limitada, em vez de indefinida. Como
esse valor é apenas informativo — e nunca decide empréstimo — 5 segundos é um preço
justo pelo ganho de latência. A alternativa (desligar o L1, ou usar *backplane* de
invalidação via pub/sub do Redis) está registrada como evolução possível.

## Invalidação

**Sempre explícita, sempre depois do commit.**

Os handlers não chamam o cache diretamente: eles enfileiram as chaves afetadas em um
`ICacheInvalidationQueue` *scoped*, e o `CacheInvalidationDecorator` — que roda **por
fora** do `TransactionDecorator` — drena a fila quando a transação commita com sucesso
([architecture.md](architecture.md#o-pipeline-cqrs)).

```csharp
// dentro do handler, ainda na transação
cacheInvalidation.Enqueue(BookCache.KeysFor(bookId));
```

Invalidar **antes** do commit seria um bug clássico: entre a remoção e o commit, uma
leitura concorrente repopularia o cache com o valor **antigo**, e esse valor sobreviveria
até o TTL — com o banco já contendo o valor novo. Invalidar depois do commit torna a
janela igual ao tempo de uma chamada ao Redis.

### Quem invalida o quê

| Operação | Chaves removidas |
|---|---|
| `POST /loans` | `book:v1:{id}`, `book:v1:{id}:availability` |
| `POST /loans/{id}/return` | idem, do livro do empréstimo |
| `POST /loans/{id}/cancel` | idem |
| `PATCH /books/{id}` | idem (título, autor ou quantidade mudaram) |
| `DELETE /books/{id}` | idem (`isActive` mudou) |
| `POST /books` | nenhuma (não existe entrada anterior para o id novo) |

Optou-se por **remover** e não por **atualizar** a entrada: escrever o valor novo no
cache a partir do resultado do comando parece uma otimização, mas reintroduz a corrida
que a invalidação evita (dois comandos concorrentes escrevendo valores em ordem
arbitrária no Redis, o mais velho vencendo). Remover é sempre seguro: o próximo leitor
busca no banco, que é a verdade.

### Se a invalidação falhar

Redis indisponível no momento do `Enqueue`/drenagem: a falha é **registrada e contada**
(`biblioteca.cache.invalidation.failed`), mas **não** reverte a transação — o empréstimo
já commitou e desfazê-lo por causa do cache seria trocar um problema de latência por um
de corretude. O TTL curto limita a exposição: no máximo 30s de disponibilidade velha.
A métrica existe justamente para que isso seja visível em vez de silencioso.

## Redis fora do ar

| Efeito | Comportamento |
|---|---|
| Leituras cacheadas | Caem para o PostgreSQL; latência sobe, respostas continuam corretas |
| Escritas | Não afetadas — o caminho de escrita não depende do Redis |
| `GET /health/ready` | Reporta o Redis como degradado, mas **continua `Healthy`** |
| `GET /health/live` | Não consulta o Redis; continua `Healthy` |

A decisão de não reprovar a readiness por causa do Redis é deliberada: se o Redis cair,
reprovar a readiness tiraria **todas** as réplicas do balanceador e transformaria "o
sistema está mais lento" em "o sistema está fora do ar". Ver
[observability.md](observability.md#health-checks).

## ETag e `304`

`GET /books/{id}` responde `ETag` derivado do `xmin` da linha. Com `If-None-Match`
correspondente, a resposta é `304 Not Modified`, sem corpo — e sem consultar Redis nem
PostgreSQL, porque o valor do `ETag` é resolvido a partir da entrada em cache.

É a camada de cache mais barata que existe: o dado não sai do servidor. E o mesmo `ETag`
serve à concorrência otimista do `PATCH` ([concurrency.md](concurrency.md)), o que
significa um conceito só cumprindo dois papéis.

## Como isso é testado

`CacheTests` (integração, Redis real via Testcontainers):

| Teste | Verifica |
|---|---|
| `Availability_SecondRead_HitsCache` | Segunda leitura não gera query no PostgreSQL (contador de comandos do Npgsql) |
| `Loan_InvalidatesAvailability` | Após `POST /loans`, `GET /availability` reflete o valor novo **imediatamente**, sem esperar o TTL |
| `Return_InvalidatesAvailability` | Idem, na devolução |
| `PatchBook_InvalidatesBook` | Após `PATCH`, `GET /books/{id}` devolve os dados novos e `ETag` novo |
| `RedisDown_ReadsStillWork` | Com o container do Redis parado, `GET /books/{id}` responde `200` a partir do banco |
| `IfNoneMatch_Returns304` | `ETag` correspondente → `304` sem corpo |
