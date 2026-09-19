# OMS: idempotência e aplicação de fills

O aggregate `Order` mantém o estado de execução recebido da venue sem depender
de ordenação de entrega. Cada fill possui um `ExecutionId` estável, quantidade,
preço, timestamp UTC e chave de idempotência.

## Contrato

- O primeiro fill é aplicado uma única vez e atualiza `FilledQuantity`,
  `RemainingQuantity` e o status (`PartiallyFilled` ou `Filled`).
- A repetição do mesmo `ExecutionId` com dados idênticos retorna um resultado
  marcado como `IsDuplicate` e não altera o aggregate.
- O mesmo `ExecutionId` com dados diferentes falha fechado; isso sinaliza
  corrupção, colisão de identidade ou replay inconsistente.
- A soma dos fills nunca pode ultrapassar a quantidade original da ordem.
- Fills novos depois de `Filled`, `Cancelled`, `Rejected` ou `Expired` são
  rejeitados.
- Eventos podem chegar fora de ordem. O timestamp original do fill é
  preservado, mas `UpdatedAtUtc` nunca retrocede.
- O transition resultante continua sendo persistido pelo boundary de
  persistência antes de qualquer I/O da exchange.

Esta fatia cobre o comportamento de domínio do AC-OMS-02. O adapter de
exchange e a persistência devem mapear o trade/execution ID da venue para
`OrderFill.ExecutionId`; testes de contrato e uma execução Testnet autenticada
continuam sendo gates separados.

## Evidência local

```bash
export PATH="/home/ernane/.dotnet:$PATH"
dotnet test tests/CryptoTrading.Domain.Tests/CryptoTrading.Domain.Tests.csproj --no-restore
```

Os testes cobrem partial fill, replay idempotente, evento fora de ordem,
duplicata conflitante e overfill. Eles não enviam ordens nem acessam a rede.
