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
