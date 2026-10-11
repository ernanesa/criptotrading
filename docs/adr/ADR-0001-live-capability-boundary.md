# ADR-0001 — Capability Live isolada e bloqueada por promoção independente

**Estado:** aceito para arquitetura; a capability Live continua não implementada e desabilitada.

## Contexto

O runtime atual aceita somente `PAPER` e `TESTNET`, inicia com `NO_TRADE=true` e os adaptadores mutáveis existentes são exclusivos de Testnet. O `LivePromotionContractValidator` valida um manifesto assinado, mas é um contrato puro: não é chamado pelo runtime e não habilita uma ordem.

Uma extensão direta do adaptador Testnet para Live compartilharia flags, credenciais e endpoints entre ambientes. Isso aumenta o risco de usar uma chave ou conta no ambiente errado, especialmente após um resultado `UNKNOWN` ou uma recuperação parcial.

## Decisão

Se uma capability Live for implementada depois dos gates prévios, ela terá uma boundary própria. Ela não reutilizará registro de DI, cliente, flags, namespace de credenciais, configuração de endpoint ou aprovação de Testnet.

O runtime Live deve permanecer ausente até que todos estes itens estejam presentes e válidos no momento da inicialização e antes de cada submit:

1. promoção humana independente, verificável e ainda válida, ligada ao SHA do artefato, hash da configuração e hash da política de risco;
2. identidade e escopo explícitos de venue, conta, símbolo, capital e janela;
3. origem e rotação da chave pública que verifica a promoção, além de uma forma durável de revogar uma aprovação ou credencial;
4. credenciais Live file-scoped, com permissões mínimas, segregadas das de Testnet e sem saque, transferência, margem ou futures;
5. pré-validação atual de filtros, saldo, taxas, preço, dados de mercado, exposição, lease/fencing e estado da reconciliação;
6. persistência da intenção antes do submit, `clientOrderId` idempotente e reconciliação antes de qualquer retry após estado ambíguo;
7. KillSwitch/SafeMode consultados na fronteira final de submissão e audit trail append-only redigido para todo o ciclo de vida.

Qualquer ausência, expiração, divergência, relógio inválido, estado `UNKNOWN`, perda de lease ou indisponibilidade da reconciliação resulta em `NO_TRADE`. A implementação não pode fazer retry cego nem trocar automaticamente para Testnet/PAPER.

## Alternativas consideradas

| Alternativa | Decisão | Motivo |
|---|---|---|
| Estender o cliente Testnet existente | Rejeitada | Mistura ambientes, credenciais e flags de execução. |
| Boundary Live isolada | Escolhida | Torna o escopo, a revogação e a revisão de segurança verificáveis. |
| Execução manual externa | Possível somente como operação humana | Não implementa uma capability Live automatizada. |

## Consequências e sequência

Antes de código Live, fechar a política financeira versionada, OOS/soak, recuperação, segurança de host e a revisão independente de risco. Depois, especificar o contrato de promoção v2 para incluir venue, conta, key provenance e revogação; o contrato atual é insuficiente para autorizar runtime.

Somente então implementar um adaptador Live isolado, testes de contrato sem rede mutável, simulações de timeout/fill parcial/reconexão e revisão crítica de segurança, concorrência, persistência e OMS. Um GO humano separado, com expiração, continua obrigatório imediatamente antes de qualquer janela futura.

## Evidência atual

- `src/CryptoTrading.Worker/WorkerRuntimeOptions.cs` rejeita `LIVE`.
- `src/CryptoTrading.Worker/WorkerExecutionRegistration.cs` registra apenas execução PAPER/Testnet.
- `src/CryptoTrading.Exchange.Binance/BinanceSpotOrderClient.cs` restringe o cliente mutável a Testnet.
- `src/CryptoTrading.Configuration/LivePromotionContract.cs` não tem call site no runtime.

Este ADR não cria endpoint, ambiente, credencial ou permissão Live e não autoriza Testnet mutável, deploy ou ordem real.
