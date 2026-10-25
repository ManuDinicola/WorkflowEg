# Web UI

Open `http://localhost:8080/ui` (the root `/` redirects there). The page is built into the engine (one embedded file, no extra install) and works in the Windows service and the single-file release.

- **API key**: enter it in the top right; it is kept in the browser session only and sent as `X-Api-Key` with every call. With `--insecure-no-auth` (development) leave it empty. The page itself is public; everything it shows comes from the authenticated API, so a key with fewer roles sees less.
- **Overview**: instances by state, open incidents, open tasks, jobs by state.
- **Instances**: newest first, filter by state and process id. Click one for the diagram with a red marker on every running element (number = tokens), its variables, incidents and history; cancel a running instance or delete a finished one.
- **Incidents**: open incidents with a Resolve button.
- **Tasks**: open user tasks. Open one to fill its form (drawn from the deployed form schema; without a form you enter the variables as JSON) and complete it; validation errors from the form are shown.
- **Deploy**: upload a `.bpmn`, `.dmn` or `.form` file, or edit BPMN XML in the text box, preview the diagram and deploy it.
- **Analytics**: pick a process to see instance counts, average and longest duration, and the diagram as a heat map (count and average seconds per element).

Diagrams use the BPMN diagram information (`bpmndi`) when the file has it; otherwise the page lays the elements out left to right.

## Limits
- The editor is a text editor with preview, not a graphical modeler (you cannot drag shapes).
- There is no login page: access is by API key. OIDC single sign-on for the UI is not built.
- Verified by loading the page in a headless DOM against a live engine (all tabs, diagram, token marker, heat map); it has not been checked in every browser.

## Modeler
The **Modeler** tab is a graphical BPMN editor (bpmn.io, MIT-style license with the rule that its watermark stays in place). Its files are embedded in the Flowd executable and served from `/ui/vendor/`, so it needs no internet. Draw with the palette, open a file or a deployed process, then **Deploy** or **Download**. The side panel edits what Flowd needs: the name, the job type and retries of service, send and business rule tasks, assignee and candidate groups of user tasks, and the FEEL condition of a flow. Other Flowd settings (timers, messages, multi-instance, input and output mappings, listeners, connectors) are not in the panel yet: edit them in the XML editor of the Deploy tab. The modeler keeps `flowd:` elements it does not know, so it does not lose them.

## Sign in with OpenID Connect
Set `Flowd:Ui:Oidc:ClientId` (a public client registered at your identity provider, with `https://your-host/ui` as an allowed redirect URI) and, if it differs from `Flowd:Auth:JwtAuthority`, `Flowd:Ui:Oidc:Authority`; `Flowd:Ui:Oidc:Scopes` defaults to `openid profile`. The page then shows **Sign in**. It uses the authorization code flow with PKCE in the browser: no client secret and no server session. The access token it receives is kept in the browser tab's session storage and sent as `Authorization: Bearer` to the API, which validates it like any other JWT (so the provider must issue tokens the `Flowd:Auth` settings accept, including the role and tenant claims). There is no refresh token: when the token expires you sign in again. The identity provider must allow cross-origin calls to its discovery and token endpoints from the UI origin, as providers normally do for browser apps. The flow was checked in a headless browser against a small mock provider (PKCE challenge, state check, token use); it was not run against a real provider such as Keycloak or Entra ID.

## Modeler: properties panel, check, image export, imported templates
- **Panel sections** for the selected element: *Timer* (duration, date or cycle), *Message* (name and correlation key; also on receive tasks), *Signal*, *Error* and *Escalation* codes, *Condition* (conditional events), *Multi-instance* (list, sequential or parallel, output collection, completion condition), *Variable mappings* (inputs and outputs as FEEL source → variable rows) and *Listeners* (event → job type rows). Messages, signals, errors and escalations are created as root elements of the file when you first fill them in. Every edit goes through the modeler's command stack, so undo (Ctrl+Z) works.
- **Check** sends the diagram to `POST /v1/deployments/validate` and lists the problems; click a problem to select its element.
- **Export SVG / Export PNG** save the diagram as an image.
- **Templates**: import a JSON file of your own element templates, for example `[{"id":"orders-api","name":"Orders API","connector":"http","fields":["url","body"],"defaults":{"method":"POST","url":"https://orders.example.com/api"}}]`. `connector` is one of http, email, rabbitmq, graphql, slack, sql, kafka; `fields` are the settings the panel shows; `defaults` are filled in when the template is chosen. Imported templates are kept in this browser only (they are not stored on the server).
- Not in the panel: process-level settings such as version tags, call activity settings, user task forms and due dates, task headers and boundary event details beyond the sections above: use the XML editor of the Deploy tab for those.
