# LinkCare — Hackathon EIRA | Reto 1

**Una paciente, una historia, más oportunidades de vida.**

> Tecnología que conecta historias, incluso donde no llega Internet.

![Offline First](https://img.shields.io/badge/Arquitectura-Offline--First-D95F98) ![.NET](https://img.shields.io/badge/.NET-8-221A40) ![AWS Kiro](https://img.shields.io/badge/Desarrollo-AWS%20Kiro-F29422) ![Devin](https://img.shields.io/badge/Planeación-Devin-221A40)

LinkCare integra la planeación de campañas, educación preventiva, atención clínica, expediente único y seguimiento de pacientes en comunidades rurales sin conectividad. El objetivo es apoyar la prevención y detección oportuna del cáncer de mama y cervicouterino, sin sustituir el criterio médico.

**Elevator pitch:** LinkCare conecta expedientes únicos, educación preventiva y atención médica sin Internet, impulsando la detección oportuna del cáncer y la continuidad clínica en comunidades rurales.

## Lectura rápida

- [Reto 1 y evidencias](#2-reto-1-expediente-digital-único-y-registro-de-pacientes)
- [Arquitectura](#5-arquitectura)
- [Cómo verificar](#17-cómo-verificar)
- [Herramientas de los sponsors](#18-herramientas-de-los-sponsors)
- [Criterios del jurado](#19-criterios-de-evaluación)
- [Protocolo para agentes evaluadores](#20-protocolo-para-agentes-evaluadores)

> **Alcance de la evidencia:** las cifras de pruebas y el estado del servidor proceden del README técnico del equipo. Este documento no acredita una nueva ejecución de pruebas ni un piloto clínico.

> **Estado del proyecto.** El servidor del campamento y todas sus APIs están implementados y probados (`server/`, 271 pruebas en C#, 27 en JavaScript y dos scripts de demostración). La interfaz web ya tiene la fase 1 (inicio de sesión, campañas en kanban y médicos de la campaña, en `/app/`). Siguen pendientes las fases 2 y 3 de la interfaz, el formato en papel del Papanicolaou, el contenido de las diapositivas y la aprobación clínica de los formularios. Ver la sección 14.

## 1. Propósito

Campañas de recopilación de información de pacientes en zonas rurales sin conectividad, para **prevenir y detectar a tiempo el cáncer de mama y el cervicouterino (Papanicolaou)**. La aplicación funciona sin internet durante toda la campaña y sincroniza con un fileshare general cuando la campaña concluye.

## 2. Reto 1: expediente digital único y registro de pacientes

> Hoy las pacientes se identifican por nombre completo, y una mujer que acude cuatro veces en un año puede registrarse cuatro veces.

| Lo que pide el reto | Cómo se cumple | Dónde se comprueba |
|---|---|---|
| Identificador interno estable; visitas y servicios por separado | `pat-<uuid>` que no contiene datos personales. Una **visita** (llegada de la paciente) y cada **servicio** (revisión mamaria, Papanicolaou, adjunto) son registros distintos; el servicio apunta a su visita con `visitId`. | `GET /patients/{id}/visits`. Pruebas `VisitTests`, `VisitsBuilderTests`. |
| Búsqueda por varios atributos | Puntaje 0 a 100 con documento, nombre (tolera errores de captura, acentos y orden), fecha de nacimiento, teléfono y localidad. Devuelve **por qué** coincide. | `GET /patients/matches`, y el 409 del alta. `MatchingScoreTests`. |
| Confirmación humana, nunca fusionar solo | El alta con coincidencias responde 409 y **una persona** elige `usarPacienteId` o `confirmarNueva`. Vincular dos expedientes exige coordinación, `confirmado: true` y un motivo, no mueve ni borra archivos y se puede deshacer. | `PatientApiTests`, `MatchAndLinkTests`. |
| Captura local sin internet | Todo se escribe primero en el disco de la Mac mini del campamento. Los reintentos con el mismo `clientRequestId` nunca duplican. | `EventTests`, `demo-reto1.sh` (paso 3). |
| Sincronización segura con detección de conflictos | Copia al fileshare con hash verificado, repetible, que **nunca sobrescribe** un archivo distinto (lo reporta como conflicto). Los cambios de datos de una paciente detectan conflictos por dato (`identity_conflict`). | `FileShareSyncTests`, `IdentityUpdateTests`, `demo-reto1.sh` (pasos 6 a 8). |
| Actualizar solo lo necesario | `PATCH …/identity` envía únicamente los campos que cambian, con la versión de identidad que vio quien edita. Datos distintos se combinan; el mismo dato con otro valor es conflicto. | `IdentityRulesTests`, `IdentityUpdateTests`. |
| Roles y bitácora de cambios | Tres perfiles con sesión más la paciente sin acceso (sección 3). Bitácora de cambios del expediente, bitácora de **quién lo consultó** y bitácora general de la campaña. | `GET /patients/{id}/changelog`, `/access-log`, `GET /audit`. |

Lo que demuestra el prototipo en un día, con un solo comando: `server/scripts/demo-reto1.sh` (alta ficticia, coincidencia, segunda visita con historial, trabajo sin conexión y reconexión verificada por hash, conflicto, y control de acceso).

## 3. Perfiles

| Perfil | Quién es | Qué puede hacer |
|---|---|---|
| **Coordinador general** (`coordinador-general`) | Ve todas las campañas | Crea campañas, administra usuarios, importa historial, arma minicubos, sincroniza el fileshare, ve el tablero global, exporta a Excel y consulta toda la bitácora. |
| **Coordinador de campaña** (`coordinador-campana`) | Administra la suya | Avanza las etapas, sube documentos legales, controla las presentaciones, ve el tablero y el Excel de **sus** campañas, sincroniza, confirma o deshace vínculos entre expedientes y consulta la bitácora de su campaña. |
| **Médico especialista** (`medico`) | Captura en su consultorio | Registra pacientes, abre visitas, captura estudios y adjuntos **solo con su consultorio y en sus campañas**, y consulta el expediente y el historial. |
| **Paciente o prospecto** | Sin acceso al sistema | No inicia sesión. Usa el **sitio público de autocapacitación** (podcast y plática en grupo). |

Cada coordinador de campaña y cada médico tiene asignadas sus campañas; fuera de ellas recibe 403. Cambiar el rol, el consultorio o las campañas de un usuario, o desactivarlo, invalida su sesión anterior al momento.

## 4. Etapas de la campaña

Una campaña avanza una etapa a la vez y nunca retrocede. Solo una campaña por servidor puede estar en campo.

| Etapa | Qué ocurre | Qué permite el sistema |
|---|---|---|
| 1. **asignada** | El gobierno asigna el espacio. | Solo documentos que dan certeza legal (convenios, oficios). No se captura. |
| 2. **concientizacion** | Llegada al espacio. Mientras se monta el laboratorio, el líder de la comunidad y una persona de EIRA dan pláticas. | Presentación en grupo coordinada con SignalR y podcast de audios. No se captura. |
| 3. **consulta** | El laboratorio está instalado. | Registro de pacientes, visitas, estudios y adjuntos. Es la única etapa de captura. |
| 4. **concluida** | La coordinación revisa lo ocurrido. | Se rechazan escrituras nuevas (los reintentos de lo ya procesado siguen respondiendo). Sincronización con el fileshare, tablero y Excel. |

Cada cambio de etapa queda registrado con quién y cuándo (`transiciones` en `campaign.json`). Concluir espera a que terminen las escrituras en curso.

## 5. Arquitectura

```
Consultorios (médicos) ─┐
Coordinación ───────────┼─ red local del campamento ─► Mac mini (servidor del campamento)
Comunidad (pacientes) ──┘   (sin internet)               ├─ API .NET 8 + SignalR
                                                         ├─ Disco local: fuente de verdad
                                                         └─ Sincronización ──► Fileshare general (Azure File Share)
                                                                                    │
                                              Servidor central sobre el fileshare ──┴─► Tablero · Excel · Minicubo
                                                                                              │
                                              (antes de la siguiente campaña, con internet) ◄─┘
```

- **Disco local primero.** Cada campaña escribe solo en sus propias rutas: un escritor por archivo, sin conflictos entre campañas.
- **Eventos inmutables.** Cada visita, estudio, adjunto o cambio de datos es un archivo que no se modifica. `state.json` y los índices se derivan y se reconstruyen; por eso no se sincronizan.
- **Dos almacenamientos** detrás de las mismas interfaces: disco (producción) y memoria (pruebas). Una batería de contrato común comprueba que se comportan igual.

### Despliegue portátil y multiplataforma

La red Wi-Fi local permite a médicos, coordinadores y participantes comunicarse con el servidor sin Internet. Puede establecerse mediante un router o punto de acceso; no se presupone que la aplicación cree el hotspot por sí misma. El servidor concentra las escrituras y el almacenamiento.

El diseño utiliza .NET y Avalonia para permitir el uso de equipos convencionales macOS o Windows (Mac mini, MacBook o laptop Windows). El despliegue de referencia documentado es Mac mini; la interfaz Avalonia y la validación de la distribución en ambos sistemas operativos siguen pendientes de comprobación en el repositorio. La interfaz web tiene una primera fase implementada.

```mermaid
flowchart LR
    A[Planeación e historial previo] --> B[Campaña: red local sin Internet]
    B --> C[Concientización y educación]
    C --> D[Consultas y expediente único]
    D --> E[Cierre local]
    E --> F[Sincronización cuando haya Internet]
    F --> G[Indicadores e historial para futuras campañas]
    G --> A
```

## 6. Datos en disco

```
{DataRoot}/
├── _config/users.json                       usuarios, perfiles y campañas asignadas
├── _global/  patients-index.json · patient-links.json
├── schemas/  envelope · mama · papanicolaou · visita · adjunto · correccion · actualizacion-datos
├── patients/pat-<uuid>/
│   ├── identity.json                        identidad inicial (quién y cuándo la creó)
│   ├── state.json                           consolidado (derivado)
│   └── campaigns/<campaña>/
│       ├── 2026/08/18/T093000-mama-c02-<id>.json     un archivo por visita, estudio o cambio
│       └── media/att-<id>.png
├── campaigns/<campaña>/
│   ├── campaign.json                        etapa, historial de etapas, unidad geográfica
│   ├── consultorios/ · documentos/          documentos legales con hash
│   └── _global/ requests/ · audit/ · sync-manifest.json · sync-report.json
├── capacitacion/ audio/<Tema>/*.mp3 · diapositivas/<id>/deck.json
└── _exports/mc-<fecha>-<id>/                minicubos
```

Los identificadores y los nombres de archivo los genera siempre el servidor: nada de lo que envía un cliente forma parte de una ruta.

## 7. Formato de revisión mamaria

El esquema `mama` v1 reproduce los **24 campos del formato en papel de EIRA** (`Images/CapturaDatos/Pag01.jpeg` y `Pag02.jpeg`): folio y cartilla, somatometría, afiliación, motivo, factores de riesgo (menarca, familiares, FUM, anticoncepción, menopausia, mastografía previa), evaluación (exploración, termografía, ultrasonido, responsable y cédula), BI-RADS 0 a 5, conducta y motivo de referencia. Se valida cada opción del formato (por ejemplo, BI-RADS fuera de 0 a 5 se rechaza). Solo `fechaAtencion` es obligatoria, para poder guardar capturas parciales.

- **Papanicolaou:** falta el formato en papel; el esquema es **provisional** (`fechaAtencion`, `resultado`, `sospechosa`).
- **BT y AT** aparecen en el encabezado del formato sin explicar su significado: se guardan como texto.
- La transcripción salió de fotografías y **debe validarla el equipo médico**.

**Casos sospechosos del tablero.** Mama: BI-RADS 4 o 5. Papanicolaou: marcado `sospechosa`. Son hallazgos de alerta del tamizaje, **no diagnósticos**. Casos para seguimiento: BI-RADS 0 o 3. Las correcciones (`datos`) modifican las cifras sin tocar el archivo original.

## 8. Autocapacitación (sin sesión)

- **Podcast.** Los audios de `audios/CancerMama` y `audios/Papanicolao` (18 episodios) se publican por tema con título en español (`tema.json` y `episodios.json` junto a los mp3). Se escuchan en la página `/capacitacion/`, por API con **soporte de rangos** y como **RSS** (`/capacitacion/podcast.xml`) para cualquier aplicación de podcasts. Para publicar más, basta copiar archivos a la carpeta.
- **Plática en grupo.** La coordinación cambia la diapositiva y todas las pantallas conectadas la reciben en vivo por **SignalR** (`/hubs/presentacion`). Quien no pueda usar SignalR consulta `GET /campaigns/{id}/presentacion`; la página pública lo hace sola con `?campana=<id>`. Solo funciona con la campaña en campo (etapas 2 y 3) y solo la coordinación de esa campaña puede cambiar la diapositiva.
- Las presentaciones son carpetas con `deck.json` y las imágenes. **Aún no hay diapositivas de contenido.**

## 9. Tablero, Excel y minicubo

- **Tablero** (`GET /campaigns/{id}/stats`, `GET /stats`): pacientes registradas y atendidas, **nuevas y recurrentes**, visitas, estudios por tipo, casos sospechosos y para seguimiento, distribución BI-RADS, adjuntos, y desglose por consultorio y por día. Una paciente es recurrente si ya tenía atenciones en otra campaña anterior (incluso con expedientes vinculados).
- **Excel** (`GET /campaigns/{id}/export.xlsx`, `GET /exports/campaigns.xlsx?campaignIds=a,b`): para patrocinadores y gobierno, con hojas Resumen, Consultorios, Por día y BI-RADS. **Solo cifras agregadas**: sin nombres, documentos ni identificadores de pacientes, y los textos se guardan como texto, nunca como fórmula.
- **Minicubo** (`POST /minicubes`): antes de salir, con internet, se arma un paquete con el historial concentrado (texto e imágenes) de las campañas concluidas de una **unidad geográfica**. Lleva un `minicubo.json` con el resumen por paciente (visitas, estudios, BI-RADS, referencia, adjuntos) y los archivos con sus hashes. En el campamento se importa (`POST /campaigns/{id}/history-import`): el historial queda de solo lectura, el médico ve qué pasó en consultas anteriores y el alta reconoce a la paciente recurrente **sin internet**. Se descarga como zip. Al terminar puede borrarse lo importado.

## 10. Sincronización con el fileshare general

`POST /campaigns/{id}/sync` copia la campaña al fileshare con la misma estructura: eventos, adjuntos, documentos, identidades, bitácora y `campaign.json`. Verifica el hash de cada archivo, se puede repetir, no crea carpetas si el volumen no está montado y **nunca sobrescribe** un archivo distinto: lo reporta como conflicto. Un servidor central que arranca sobre el fileshare reconstruye los estados derivados y ofrece el tablero y el minicubo.

## 11. Seguridad y privacidad

Son datos de salud. Medidas implementadas: HTTPS con autoridad certificadora propia (`scripts/make-local-ca.sh`), sesiones JWT con revalidación contra el usuario guardado, bloqueo tras 5 intentos fallidos, permisos por perfil, consultorio y campaña, auditoría de escrituras y de **cada consulta de expediente**, ningún dato personal en logs ni en errores, escritura atómica, rutas validadas, tipo de archivo por firma real y hash de integridad. Pendiente fuera del software: FileVault, respaldo cifrado, normativa aplicable y región de la nube.

## 12. APIs

La referencia viva está en `/swagger/v1/swagger.json`. Resumen:

| Área | Rutas |
|---|---|
| Salud y sesión | `GET /health` · `GET /health/details` · `POST /auth/login` · `GET /auth/me` |
| Usuarios (general) | `GET/POST /users` · `PUT /users/{usuario}` |
| Campañas | `POST /campaigns` (general) · `GET /campaigns` · `GET /campaigns/active` · `GET /campaigns/{id}` · `POST /campaigns/{id}/transitions` · `POST /campaigns/{id}/close` · `GET /campaigns/summary` (tarjetas con cifras y luces) · `GET /campaigns/{id}/medicos` (médicos que participan y su actividad) |
| Documentos legales | `POST/GET /campaigns/{id}/documents` · `GET …/documents/{docId}` |
| Pacientes | `POST /campaigns/{id}/patients` · `GET /patients?q=` · `GET /patients/matches` · `GET /patients/{id}` · `…/events` · `…/events/{eventId}` · `…/visits` · `…/changelog` · `…/access-log` |
| Vínculos | `GET/POST /patients/{id}/links` · `POST /patients/{id}/unlink` |
| Captura (médico) | `POST …/patients/{id}/visits` · `PATCH …/patients/{id}/identity` · `POST …/patients/{id}/events` · `POST …/patients/{id}/attachments` · `GET …/patients/{id}/attachments/{adjuntoId}` |
| Tablero y Excel | `GET /campaigns/{id}/stats` · `GET /stats` · `GET /campaigns/{id}/export.xlsx` · `GET /exports/campaigns.xlsx` |
| Sincronización | `POST/GET /campaigns/{id}/sync` |
| Historial | `POST /minicubes` · `GET /minicubes` · `GET /minicubes/{id}/download` · `POST /campaigns/{id}/history-import` · `POST /history/purge` |
| Autocapacitación (pública) | `GET /capacitacion/temas · episodios · episodios/{id}/audio · podcast.xml · presentaciones · presentaciones/{id} · presentaciones/{id}/diapositivas/{n}` · `GET/PUT /campaigns/{id}/presentacion` · hub `/hubs/presentacion` |
| Administración | `GET /audit` · `POST /admin/rebuild` |

Errores en formato `ProblemDetails` con un `code`: `validation_error`, `forbidden`, `unauthorized`, `not_found`, `conflict`, `campaign_state`, `campaign_closed`, `campaign_conflict`, `duplicate_candidates`, `identity_conflict`, `file_too_large`, `file_type_not_allowed`, `integrity_error`, `insufficient_storage`, `too_many_requests`.

## 13. Guías de referencia (carpeta `Guides/`)

| Guía | Uso |
|---|---|
| `OKYSOFT_FILESYSTEM_DRIVEN_CLEAN_ARCHITECTURE` | Carpetas por entidad, eventos inmutables, `state.json` derivado, File Share. Base del almacenamiento. |
| `OKYSOFT_ABSTRACT_FACTORY_SERVICE_DECOUPLING` | Interfaces con implementación real y en memoria. |
| `STATE_MACHINE_GUIDE` | Etapas de la campaña y estados de la sincronización. |
| `OKYSOFT_GUIA_BITACORA` | Bitácora de decisiones del proyecto. |
| `OKYSOFT_AZURE_MAPS_WEB_INTEGRATION` | Posible mapa de cobertura en el panel central (requiere internet). |
| `I18N_GUIDE`, `OKYSOFT_DATA_DRIVEN_ARCHITECTURE_HTML_SPA` | Referencia de ideas: están escritas para HTML/JS, no para Avalonia. |
| `PORTAL_SPOKE_ARCHITECTURE`, `OKYSOFT_PLANO_CONVERSION_PIPELINE` | No aplican. |

## 14. Qué falta y preguntas abiertas

**Pendiente de construir**
1. **Interfaz Avalonia** y la cola de envío del cliente para tolerar cortes breves de la red del campamento (el servidor ya garantiza que reintentar no duplica).
2. **Formato del Papanicolaou** (la foto del formato en papel) y su esquema definitivo.
3. **Diapositivas** de la plática (contenido y quién las prepara).
4. **Sincronizar los vínculos entre expedientes** (`patient-links.json`) con el fileshare: hoy no viajan.

**Preguntas para el equipo**

| # | Pregunta |
|---|---|
| 1 | ¿Qué significan **BT** y **AT** en el formato mamario? ¿El campo **ID** es la CURP? ¿Cómo se forma el **Folio Eira**? |
| 2 | ¿Validan los criterios de **caso sospechoso** (BI-RADS 4 o 5; Papanicolaou a definir) y de **seguimiento** (BI-RADS 0 o 3)? |
| 3 | ¿Debe exigirse al menos un **documento legal** antes de pasar de *asignada* a *concientización*? Hoy no se exige. |
| 4 | ¿Hace falta un perfil de **recepción** distinto del médico para el registro? Hoy el médico registra. |
| 5 | ¿Quién confirma los vínculos entre expedientes? Hoy, la coordinación (general o de campaña). |
| 6 | ¿Qué columnas exactas necesitan **patrocinadores y gobierno** en el Excel? |
| 7 | ¿Qué normativa de datos de salud aplica y en qué región puede estar el fileshare? |
| 8 | ¿Qué dispositivos usará el personal (laptops macOS y Windows, tablets)? |
| 9 | ¿El historial de una unidad geográfica puede viajar con **nombres y documentos** en el minicubo? Se instala en la Mac mini cifrada y se borra al cerrar. |

## 15. Riesgos

| Riesgo | Mitigación |
|---|---|
| La Mac mini es un único punto de falla. | UPS, respaldo incremental (`scripts/backup.sh`), reinicio automático, datos que sobreviven a `kill -9`. |
| Duplicados entre consultorios o campañas. | Puntaje por varios atributos, historial importado, vínculo humano y reversible. |
| El minicubo expone datos de muchas pacientes. | Solo la unidad geográfica, disco cifrado, borrado al cerrar la campaña (`/history/purge`). |
| Contenido clínico sin validar. | El esquema valida estructura; los médicos deben aprobar campos y criterios. |
| Reloj desajustado de la Mac mini. | Verificar la hora al iniciar la campaña; las horas de cada paciente son siempre crecientes. |
| Sitio de autocapacitación abierto en la red local. | Solo sirve contenido público de la carpeta de capacitación; ningún dato de pacientes. |

## 16. Épicas para las historias de usuario

**Coordinador general:** crear campañas y asignar su coordinación · administrar usuarios · ver todas las campañas y el tablero global · exportar a Excel para patrocinadores y gobierno · armar el minicubo de una unidad geográfica · sincronizar y consultar la bitácora completa.
**Coordinador de campaña:** subir documentos legales · avanzar las etapas · proyectar la plática en grupo · ver el avance de su campaña · confirmar o deshacer vínculos entre expedientes · sincronizar al concluir · consultar quién vio cada expediente.
**Médico especialista:** buscar o registrar una paciente y confirmar coincidencias · abrir la visita · capturar la revisión mamaria (24 campos) y el Papanicolaou · adjuntar documentos o imágenes · actualizar solo los datos que cambiaron y resolver conflictos · ver el historial de visitas y de campañas anteriores.
**Paciente o prospecto:** escuchar el podcast · seguir la plática en grupo en su pantalla.
**Transversales:** trabajo sin conexión con cola de envío · seguridad y bitácora · respaldo y puesta en marcha.

## 17. Cómo verificar

```bash
cd server
dotnet test                                                                   # 271 pruebas (C#)
node --test web-tests/*.test.mjs                                          # 27 pruebas (interfaz web)
./scripts/seed-demo-users.sh ./data-dev 'una-clave-de-demo'                   # un usuario por perfil
ASPNETCORE_ENVIRONMENT=Development Campana__FileShare__Root=/tmp/fileshare-demo \
  dotnet run --project src/Campana.Server                                     # http://localhost:5080
./scripts/demo.sh http://localhost:5080 'una-clave-de-demo'                   # las cuatro etapas de una campaña
./scripts/demo-reto1.sh http://localhost:5080 'una-clave-de-demo' ./data-dev /tmp/fileshare-demo   # Reto 1 (la carpeta del fileshare debe no existir)
```

Documentos: `server/README.md` (uso), `server/docs/PUESTA-EN-MARCHA.md` (Mac mini) y `.kiro/specs/servidor-campana-filesystem/` (requisitos, diseño y tareas).


## 18. Herramientas de los sponsors

| Herramienta | Participación en LinkCare | Experiencia del equipo |
|---|---|---|
| **Devin** | Transformación de ideas en requerimientos, épicas, historias de usuario y criterios de aceptación. | Ayudó a organizar el alcance y convertir la problemática social en una ruta de desarrollo concreta. |
| **AWS Kiro** | Desarrollo asistido por IA y Spec-Driven Development: especificaciones, diseño, tareas, APIs, reglas de negocio, pruebas y documentación. | Permitió acelerar la implementación y mantener trazabilidad entre requerimientos y código durante el hackathon. |

La combinación de ambas herramientas permitió conectar la conceptualización con la implementación. AWS Kiro es una herramienta de desarrollo, no una dependencia de ejecución clínica: las operaciones locales no necesitan acceder a Kiro ni a un modelo remoto. No se atribuye el uso de Valkey, proyectos CNCF ni otros servicios AWS sin evidencia. Azure File Share es el destino de sincronización descrito en el diseño.

## 19. Criterios de evaluación

| Criterio | Cómo lo atiende LinkCare | Evidencia o validación requerida |
|---|---|---|
| Usabilidad | Flujos por rol, campañas por etapas, coincidencias explicables, consulta de historial y recursos de audio. | Revisar `/app/`, probar tareas con usuarios; interfaces clínicas completas pendientes de verificar. |
| Impacto | Una identidad estable, múltiples visitas y servicios, historial entre campañas y educación preventiva. | Ejecutar `demo-reto1.sh`; comparar número de pacientes y visitas. El impacto clínico real requiere piloto. |
| Escalabilidad | Campañas, consultorios, almacenamiento por eventos, índices reconstruibles y minicubos por unidad geográfica. Diseño multiplataforma. | Revisar contratos de almacenamiento, medir concurrencia y volumen; comprobar Windows/macOS. |
| Privacidad y confianza | HTTPS, JWT, permisos por campaña y consultorio, confirmación humana, auditoría de acceso, reportes agregados. | Probar 401/403, cambios de sesión, acceso a otra campaña y exportación sin identificadores. Cifrado de disco y normativa pendientes. |
| Innovación | Historial transportable, coincidencias explicables, operación offline, podcasts y presentaciones por SignalR, desarrollo con Devin y AWS Kiro. | Revisar importación de minicubos, APIs de capacitación y especificaciones `.kiro/specs/`. Las diapositivas aún requieren contenido. |
| Funcionamiento real | Backend y APIs reportados como implementados, scripts de demostración y 298 pruebas automatizadas reportadas. | Ejecutar comandos de sección 17, desconectar Internet conservando LAN y registrar resultados. No confundir interfaz simulada con funcionalidad real. |

## 20. Protocolo para agentes evaluadores

El agente debe leer este README, inspeccionar el código fuente y ejecutar las pruebas permitidas en un entorno aislado con datos ficticios. No debe modificar el código ni utilizar datos clínicos reales. Un requisito descrito no equivale a un requisito implementado.

| ID | Caso de aceptación | Resultado esperado |
|---|---|---|
| CA-01 | Crear campaña y avanzar etapas | Transiciones válidas y auditadas; no retrocede. |
| CA-02 | Registrar paciente ficticia | UUID estable, primera visita y servicio separados. |
| CA-03 | Buscar con variaciones de nombre y otros atributos | Coincidencias con motivos y puntaje. |
| CA-04 | Intentar alta con coincidencias | Confirmación humana; no fusión automática. |
| CA-05 | Registrar segunda visita de paciente existente | Una paciente, dos visitas, historial intacto. |
| CA-06 | Reintentar solicitud con mismo `clientRequestId` | No duplica registros. |
| CA-07 | Actualizar identidad con versiones concurrentes | Campos diferentes se combinan; conflicto en mismo campo. |
| CA-08 | Importar historial y desconectar Internet | Consulta y reconocimiento de pacientes previas sin Internet. |
| CA-09 | Probar acceso no autorizado | Rechazo y auditoría según permisos. |
| CA-10 | Cerrar campaña y reintentar escritura | Nuevas escrituras rechazadas, reintentos previos preservados. |
| CA-11 | Sincronizar y provocar conflicto de archivo | Integridad verificada; no sobrescritura de archivo distinto. |
| CA-12 | Consultar podcast/presentación y exportar reporte | Contenido público sin datos clínicos; Excel agregado. |

Clasificar cada caso y criterio como **Verificado**, **Parcial**, **Simulado**, **Propuesto** o **No verificable**. Registrar ruta de archivo, prueba o comando, resultado observado y limitaciones. Si un comando no puede ejecutarse, indicarlo; no inventar resultados. Entregar una matriz de hallazgos y mejoras priorizadas, sin asignar ponderaciones oficiales no proporcionadas por el jurado.

## 21. Estado y evolución del proyecto

| Componente | Estado según documentación del equipo |
|---|---|
| Backend .NET 8, APIs y persistencia por archivos | Implementado y probado según README técnico |
| Pruebas C# / JavaScript | 271 / 27 reportadas (298 en total); requieren ejecución independiente para verificar |
| Interfaz web | Fase 1 documentada: sesión, campañas kanban y médicos |
| Avalonia y despliegue Windows/macOS | Tecnología seleccionada; finalización y validación pendientes según último README técnico |
| Podcasts | 18 episodios documentados |
| Presentaciones grupales | Servicios disponibles; contenido de diapositivas pendiente |
| Sincronización | Copia al fileshare con verificación; vínculos entre expedientes aún no sincronizados |
| Fotografías | Adjuntos documentados; extracción manuscrita automática propuesta para evolución |
| Validación clínica y piloto | Pendientes |

El almacenamiento vigente documentado es **Filesystem-Driven**, no SQLite (alternativa considerada en una fase previa). No se presenta la digitalización automática de manuscritos como una capacidad ya disponible. Antes de un piloto se requiere aprobar formularios, definir el consentimiento informado y su evidencia, verificar cifrado y respaldos, y determinar normativa y retención aplicables.

## 22. Visión

LinkCare busca que la falta de conectividad no sea una barrera para la continuidad de la atención médica. Su evolución contempla completar interfaces, validar el despliegue multiplataforma, ampliar contenidos educativos, fortalecer privacidad, incorporar digitalización documental y medir resultados en campañas reales.

**LinkCare — Una paciente, una historia, más oportunidades de vida.**

*Built with AWS Kiro and Devin. Designed for social impact.*
