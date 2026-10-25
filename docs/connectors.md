# Connectors

## HTTP connector (outbound)
A service task with a `flowd:connector` element calls an HTTP service; no worker code is needed. The engine runs a built-in worker for it (every 250 ms, all tenants).
```xml
<bpmn:serviceTask id="Notify" name="Notify shipping">
  <bpmn:extensionElements>
    <flowd:connector type="http" url="https://api.example.com/orders/{{orderId}}" method="POST"
                     body='{"customer":"{{customer.name}}"}' headers='{"Authorization":"Bearer {{secrets.SHIP_TOKEN}}"}'
                     resultVariable="shipping" failOnError="true"/>
  </bpmn:extensionElements>
</bpmn:serviceTask>
```
- `url`, `body`, `headers` (a JSON object) are templates: `{{variable}}`, `{{order.id}}` read process variables, `{{secrets.NAME}}` reads a secret. An unknown name fails the job with a clear message; it is never replaced by an empty string.
- The result variable (default `response`) gets `{ "status": 200, "body": ... }`, the body parsed as JSON when the answer is JSON.
- A non-2xx answer fails the job (retries and incidents as for any job, 5 s back-off between tries) unless `failOnError="false"`.
- Delivery is at-least-once like every job: make the called service idempotent.

### Safety
- **Allow-list**: only hosts in `Flowd:Connectors:AllowedHosts` can be called. Empty (the default) refuses every call, so nothing is reachable by accident. `*` allows any host (only for networks you trust: internal addresses become reachable).
- Redirects are never followed; the timeout is `Flowd:Connectors:TimeoutSeconds` (10) and the response is cut at `MaxResponseBytes` (1 MiB).
- **Secrets** come from configuration or the environment: `Flowd__Secrets__SHIP_TOKEN=...` (or `Flowd:Secrets:SHIP_TOKEN` in appsettings). They are used only while building the request; they are not written to variables or history. Restrict the host's settings file accordingly.

## Webhook (inbound)
Other systems can start or wake processes by posting JSON; the webhook becomes a message.
```
Flowd__Connectors__Webhooks__order-paid__Secret=topsecret
Flowd__Connectors__Webhooks__order-paid__CorrelationKey=orderId     # path in the body, default "id"
Flowd__Connectors__Webhooks__order-paid__Tenant=                    # default: no tenant
```
`POST /v1/connectors/webhook/order-paid` with the JSON body and the header `X-Flowd-Signature: sha256=<hex HMAC-SHA256 of the raw body with the secret>` (the same scheme the outgoing webhooks use). A valid call publishes the message `order-paid` with the body as variables and the correlation key from the body, buffered for one minute, and answers 202 with the number of correlated instances. A catch event, receive task or message start event named `order-paid` receives it. No API key is needed (the signature is the authentication); a wrong or missing signature is 401, an unknown name 404.

## Not built yet
E-mail and message-queue connectors. Calling a service that needs more than headers and a text body (multipart, mutual TLS) is not supported.

## E-mail connector
A service task with `<flowd:connector type="email" to="{{customer.email}}" subject="Order {{id}}" body="Hello" html="false" from="..." resultVariable="mail"/>` sends one mail through your SMTP server (the built-in worker, at least once: a retry after a crash can send twice). All text settings are templates. The result variable gets `{ "sent": <number of recipients> }`.

Settings under `Flowd:Connectors:Smtp`: `Host` (required), `Port` (587), `UseSsl` (true), `User`, `Password`, `From` (default sender), `AllowedRecipientDomains` (exact domains; empty refuses every mail, `*` allows any). Put the password in an environment variable (`Flowd__Connectors__Smtp__Password`), never in a file you commit. Line breaks in sender, subject or recipients are refused, so a variable cannot inject headers.

## Message queue connector (RabbitMQ)
A service task with `<flowd:connector type="rabbitmq" exchange="orders" routingKey="new.{{region}}" body='{"id":{{id}}}' contentType="application/json" resultVariable="published"/>` publishes one persistent message to a RabbitMQ broker and waits for the broker's confirmation before the process continues. All text settings are templates. The result variable gets `{ "published": true }`. Delivery is at least once: a retry after a crash can publish the message twice, so consumers must be idempotent. Publishing to a queue directly: leave `exchange` empty and put the queue name in `routingKey` (the default exchange).

Settings under `Flowd:Connectors:Queue`: `Address` (required, `amqp://user:password@host:5672/vhost`, or `amqps://` for TLS; keep it in an environment variable, `Flowd__Connectors__Queue__Address`) and `AllowedExchanges` (exact exchange names; empty refuses every message; `(default)` allows the default exchange; `*` allows any). Only RabbitMQ (AMQP 0-9-1) is supported. Kafka and cloud queues are not. The connector was tested against a real RabbitMQ 3.13 broker in a container.

## HTTP authentication, GraphQL and Slack
- **Authentication of the HTTP connector** (and of GraphQL and Slack, which use it): `authType="bearer"` with `token`; `authType="basic"` with `username` and `password`; `authType="oauth2"` (client credentials) with `tokenUrl`, `clientId`, `clientSecret`, optional `scope`, and `clientAuthentication="basicAuthHeader"` to send the client in a Basic header instead of the request body. Put secrets in `{{secrets.NAME}}`. The token request goes through the same host allow-list as the call (the `tokenUrl` host must be in `Flowd:Connectors:AllowedHosts`). Tokens are cached in memory on the node until 30 seconds before they expire; they are never put into variables or history. Only the client credentials flow is supported (no authorization code, no refresh tokens).
- **GraphQL**: `<flowd:connector type="graphql" url="https://api.example.com/graphql" query="query($id: ID!) { order(id: $id) { total } }" variables='{"id":"{{orderId}}"}' resultVariable="order"/>`. The result variable gets `{ status, data, errors }`; a non-empty `errors` list fails the job unless `failOnError="false"`. Only the `variables` setting is a template, so a variable value that looks like `{{secrets.X}}` is sent as plain text and never expanded.
- **Slack**: `<flowd:connector type="slack" token="{{secrets.SLACK_TOKEN}}" channel="#ops" text="Order {{id}} failed"/>` posts with `chat.postMessage`; add `slack.com` to `AllowedHosts`. The result variable (default `slack`) gets `{ ts, channel }`. Slack answers with `ok: false` fail the job with Slack's error text.
