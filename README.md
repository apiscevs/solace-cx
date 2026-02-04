# Solace Codex (.NET Aspire + Blazor)

End-to-end sample for testing **Solace PubSub+ direct messaging** with two apps:

- **Publisher** (`Solace.Publisher`): sends messages to topics
- **Subscriber** (`Solace.Subscriber`): subscribes to topics and shows inbound messages
- **Admin** (`Solace.Admin`): reads queues and queue subscriptions via SEMP v2

The solution uses .NET Aspire for orchestration and shared defaults.

## Solution layout

- `Solace.slnx`
- `Solace.AppHost` - Aspire orchestrator
- `Solace.ServiceDefaults` - shared Aspire telemetry/health defaults
- `Solace.Shared` - shared options/models/history state
- `Solace.Publisher` - Blazor Server publisher UI + hosted Solace client
- `Solace.Subscriber` - Blazor Server subscriber UI + hosted Solace client
- `Solace.Admin` - Blazor Server admin UI + SEMP client for queue metadata

## Prerequisites

- .NET SDK **10.0.102** or newer
- Solace Cloud account/credentials

## Quick start

1) Restore/build:

```bash
dotnet restore
dotnet build Solace.slnx
```

2) Configure secrets for messaging apps:

```bash
# Publisher
dotnet user-secrets set "Solace:Host" "tcps://mr-connection-aaepajf6a13.messaging.solace.cloud:55443" --project Solace.Publisher
dotnet user-secrets set "Solace:VpnName" "apservice-fr" --project Solace.Publisher
dotnet user-secrets set "Solace:Username" "solace-cloud-client" --project Solace.Publisher
dotnet user-secrets set "Solace:Password" "<your-password>" --project Solace.Publisher
dotnet user-secrets set "Solace:TopicPrefix" "solace/test" --project Solace.Publisher

# Subscriber
dotnet user-secrets set "Solace:Host" "tcps://mr-connection-aaepajf6a13.messaging.solace.cloud:55443" --project Solace.Subscriber
dotnet user-secrets set "Solace:VpnName" "apservice-fr" --project Solace.Subscriber
dotnet user-secrets set "Solace:Username" "solace-cloud-client" --project Solace.Subscriber
dotnet user-secrets set "Solace:Password" "<your-password>" --project Solace.Subscriber
dotnet user-secrets set "Solace:TopicPrefix" "solace/test" --project Solace.Subscriber
```

3) Configure secrets for the admin app (SEMP):

```bash
dotnet user-secrets set "SolaceSemp:BaseUrl" "https://mr-connection-aaepajf6a13.messaging.solace.cloud:943/SEMP/v2/config" --project Solace.Admin
dotnet user-secrets set "SolaceSemp:VpnName" "apservice-fr" --project Solace.Admin
dotnet user-secrets set "SolaceSemp:Username" "<management-username>" --project Solace.Admin
dotnet user-secrets set "SolaceSemp:Password" "<management-password>" --project Solace.Admin
```

4) Run the distributed app:

```bash
dotnet run --project Solace.AppHost
```

Open the Aspire dashboard URL printed in terminal, then open Publisher, Subscriber, and Admin from there.

## Common commands

```bash
dotnet build Solace.slnx
dotnet test
dotnet watch run --project Solace.AppHost
dotnet format
```

## Configuration model

Both apps bind `Solace` section to `SolaceOptions` (`Solace.Shared/SolaceOptions.cs`):

- `Host` (required)
- `VpnName` (required)
- `Username` (required)
- `Password` (required)
- `TopicPrefix` (default: `solace/test`)

Derived defaults:

- publish topic: `<TopicPrefix>/messages`
- subscription topic: `<TopicPrefix>/>`

## Notes and troubleshooting

- If apps look stuck in connecting state, first verify host spelling:
  - correct value includes **`aaepajf6a13`** (digit `6`, not letter `c`)
- Check secrets quickly:

```bash
dotnet user-secrets list --project Solace.Publisher
dotnet user-secrets list --project Solace.Subscriber
dotnet user-secrets list --project Solace.Admin
```

- If ports are already in use, stop old processes:

```bash
pkill -f Solace.AppHost || true
pkill -f Solace.Publisher || true
pkill -f Solace.Subscriber || true
```

- Current implementation sets `SSLValidateCertificate = false` in both clients to avoid local trust-store setup issues when using `tcps://...`.
  - For production, configure proper trust settings and enable certificate validation.

## Getting management credentials for SEMP

SEMP uses **management users**, not messaging client usernames (`solace-cloud-client`).

Typical flow in Solace Cloud:

1. `Cluster Manager` -> open your broker service (`apservice-fr`)
2. `Manage` tab
3. In `Rotate Passwords`, select the permission profile you want (for example Mission Control Manager/Editor)
4. Rotate and copy the generated password
5. Use the corresponding management username in `SolaceSemp:Username`

Verify credentials before running the Admin app:

```bash
curl -u "<management-username>" "https://mr-connection-aaepajf6a13.messaging.solace.cloud:943/SEMP/v2/config/msgVpns/apservice-fr/queues"
```

If the call returns JSON with `responseCode: 200`, your SEMP credentials are valid.
