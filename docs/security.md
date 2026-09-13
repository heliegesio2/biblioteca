# Segurança

O enunciado não exige autenticação. Ela foi incluída por um motivo concreto: o requisito
de auditoria manda registrar **o ator** de cada mudança, e não há como registrar ator sem
alguma noção de identidade. Um campo `actor` preenchido com `"anonymous"` em toda linha
tornaria a trilha inútil exatamente no ponto que ela existe para resolver.

O escopo é deliberadamente mínimo — JWT com papéis, sem cadastro de credenciais, sem
refresh token, sem recuperação de senha.

## Autenticação

JWT Bearer assinado com chave simétrica (HMAC-SHA256). O token carrega:

| Claim | Conteúdo |
|---|---|
| `sub` | `users.id` |
| `email` | Usado como `actor` na auditoria |
| `role` | `librarian` ou `member` |
| `exp` | Expiração curta (4h) |

Validação com `ValidateIssuer`, `ValidateAudience`, `ValidateLifetime` e
`ValidateIssuerSigningKey` todos ligados, sem tolerância de relógio maior que o default.

### `POST /auth/token`

Emite um token para um `userId` existente e um papel. **Disponível apenas fora de
`Production`** — em `Production` o endpoint não é mapeado, e a aplicação recusa subir se
`Auth:SigningKey` não vier de configuração externa.

Isso é um **substituto declarado de um identity provider**, não uma proposta de desenho.
Em produção seria OIDC com chaves assimétricas e validação por JWKS; o código de
validação muda em uma linha de configuração, porque nada no domínio depende de como o
token foi emitido — só do `ClaimsPrincipal` resultante.

## Autorização

Dois papéis, com uma regra transversal.

| Operação | `librarian` | `member` |
|---|---|---|
| `POST` / `PATCH` / `DELETE /books` | ✔ | ✗ |
| `GET /books`, `GET /books/{id}`, `/availability` | ✔ | ✔ |
| `POST /users` | ✔ | ✗ |
| `GET /users/{id}/loans` | ✔ (qualquer) | ✔ (**só o próprio**) |
| `POST /loans` | ✔ (para qualquer usuário) | ✔ (**só para si**) |
| `POST /loans/{id}/return` | ✔ | ✔ (só os próprios) |
| `POST /loans/{id}/cancel` | ✔ | ✗ |
| `GET /books/{id}/history` | ✔ | ✔ |
| `GET /audit-events` | ✔ | ✗ |
| `GET /health/*`, `POST /auth/token` | anônimo | anônimo |

A regra transversal é o **acesso a recurso de terceiro**: um `member` só enxerga e
movimenta os próprios empréstimos. Ela não é expressável por política de papel — depende
do `userId` do recurso — então vive em um `IAuthorizationHandler` de propriedade
(`SameUserOrLibrarian`), aplicado aos endpoints da tabela, e **não** espalhada como `if`
dentro dos handlers.

Tentativa de acessar recurso de outro usuário responde `403 forbidden`, não `404`:
o recurso existe, e mascarar isso não protege nada num sistema em que os identificadores
já são conhecidos de quem os criou.

## Segredos

**Nada real é versionado.** O repositório contém apenas credenciais locais e
descartáveis, usadas pelo `docker-compose.yml` (`postgres`/`postgres`, banco
`biblioteca`) e uma chave de assinatura de desenvolvimento em
`appsettings.Development.json`, marcada como tal.

| Segredo | Local | Produção |
|---|---|---|
| `ConnectionStrings__Postgres` | Compose: variável de ambiente | `Secret` do Kubernetes, referenciado por nome no chart |
| `ConnectionStrings__Redis` | idem | idem |
| `Auth__SigningKey` | `appsettings.Development.json` | `Secret`, injetado por `env.valueFrom.secretKeyRef` |

O Helm chart **não contém valores de segredo**: ele referencia um `Secret` existente por
nome (`existingSecret`), o que mantém o chart versionável e evita o antipadrão de
`values.yaml` com credencial em base64 — que não é criptografia, é codificação.

Uma verificação em `Program.cs` recusa subir em `Production` com a chave de
desenvolvimento ou com chave curta demais (< 32 bytes). Falhar no start é melhor do que
servir tráfego com token forjável.

## Superfície exposta

- **Container sem root**: a imagem roda como usuário não privilegiado
  (`USER app`, UID 64198, base `mcr.microsoft.com/dotnet/aspnet:10.0`), com
  `readOnlyRootFilesystem: true` e `allowPrivilegeEscalation: false` no `securityContext`
  do Deployment.
- **Porta 8080**, não 80 — sem necessidade de capabilities privilegiadas.
- **UI do OpenAPI (`/scalar`) só fora de `Production`.** O documento OpenAPI descreve a
  superfície inteira da API; expô-lo publicamente é informação gratuita para quem for
  sondar.
- **Problem Details nunca vazam detalhe interno**: stack trace e mensagem de exceção
  ficam no log, e a resposta carrega `code`, `title`, `detail` de negócio,
  `correlationId` e `traceId`. `DeveloperExceptionPage` só em `Development`.

## Validação de entrada

- FluentValidation em todo comando e query, executada **antes** de qualquer acesso ao
  banco ([architecture.md](architecture.md#o-pipeline-cqrs)).
- Limites explícitos de tamanho em todo campo de texto, `limit` máximo de 100 na
  paginação — um `limit=1000000` não vira varredura de tabela.
- Consultas sempre por LINQ/EF parametrizado. Onde há SQL bruto (o `UPDATE` condicional é
  gerado por `ExecuteUpdateAsync`), os valores continuam parametrizados: não há
  concatenação de string com entrada de usuário em lugar nenhum.
- `Content-Type` estrito e limite de tamanho de corpo padrão do Kestrel.

## O que ficou de fora

| Ausente | Por quê / o que faria em produção |
|---|---|
| Identity provider real | Fora do escopo; OIDC + JWKS, com o mesmo `ClaimsPrincipal` chegando aos handlers |
| Refresh tokens, revogação | Exigiria armazenamento de sessão; token curto (4h) é suficiente para o desafio |
| Rate limiting | O middleware nativo do ASP.NET Core resolveria; listado na evolução do [README](../README.md#10-evolução-para-produção) |
| CORS restritivo | Não há front-end; hoje a política é vazia (nenhuma origem cruzada permitida) |
| HTTPS obrigatório | Terminação TLS é responsabilidade do ingress; o container serve HTTP na 8080 |
| Criptografia em repouso / PII | Só nome e e-mail são armazenados; um sistema real precisaria de política de retenção e anonimização |
| Trilha de auditoria imutável | Hoje a aplicação só insere; produção pediria role `INSERT`-only e/ou encadeamento por hash ([auditing.md](auditing.md#limitações)) |
