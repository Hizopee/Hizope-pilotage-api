# Hizope-pilotage-api

API d'agrégation pour le tableau de bord interne de Hizope (« Hizope-pilotage-web ») —
usage de Joyce uniquement, distinct des dashboards de chaque produit (CMicrolocks-admin,
etc.).

## Principe

Chaque backend produit (CMicrolocks-backend, puis LoveList-backend, puis Gaia) expose ses
propres routes `api/platform/*`, protégées par une clé secrète partagée (en-tête
`X-Platform-Key`) — pas par le système d'auth de ses propres utilisateurs finaux. Cette
API se contente d'appeler ces routes avec la bonne clé et de relayer la réponse : aucune
base de données ici, aucun état, juste un proxy authentifié par produit.

Modules :

- **CMicrolocks** — combien Hizope doit reverser au salon sur les acomptes
encaissés hors Stripe Connect (`GET/POST /api/reconciliation/cmicrolocks`), résumé Stripe
  (`GET /api/stripe/cmicrolocks/summary`, 10 % de frais de service), logs
  (`GET /api/logs/cmicrolocks`).
- **LoveList** — produit 100 % Hizope (abonnements Premium) : **aucune commission, rien à
  reverser**, tout l'encaissé revient à Hizope. Résumé Stripe
  (`GET /api/stripe/lovelist/summary` : brut, frais Stripe, net), virements Stripe vers le
  compte bancaire + solde disponible/en attente (`GET /api/stripe/lovelist/payouts`), logs
  applicatifs (`GET /api/logs/lovelist`, relaie `api/platform/logs` de LoveList-backend).

`/api/stripe/{product}/payouts` marche aussi pour `cmicrolocks`. Les prochains produits
ajouteront leur propre client (`XxxPlatformClient`) + leurs propres routes, sur le même
modèle.

## Sécurité

Ce service **n'a pas d'authentification applicative propre** : il n'est jamais exposé
directement sur Internet (`expose`, pas `ports`, dans le docker-compose du VPS partagé) —
seul Caddy l'atteint, et c'est Caddy qui protège tout le site `cmicrlocks.fr`
(HTTP Basic Auth, un seul compte : Joyce). Voir `hizope-scaleway-deploy/shared-vps/Caddyfile`.

## Développement

```bash
dotnet restore
dotnet run --project src/HizopePilotage.Api
```

Écoute sur `http://localhost:5080` par défaut (profil de lancement) — adapter si un autre
service tourne déjà sur ce port en local. Variables utiles en dev
(`src/HizopePilotage.Api/appsettings.Development.json`, non commité, ou `--environment`) :

```
CMicrolocks__BaseUrl=http://localhost:5081
CMicrolocks__PlatformKey=une-cle-de-dev
LoveList__BaseUrl=http://localhost:5082
LoveList__PlatformKey=une-cle-de-dev
LoveList__Stripe__SecretKey=rk_live_...
LoveList__Stripe__TestSecretKey=rk_test_...
```

(pointer vers une instance locale de `CMicrolocks-backend` avec `Platform:PilotageKey`
réglé à la même valeur dans ses propres user-secrets.)

## Tests

```bash
dotnet test
```

Les appels sortants vers CMicrolocks sont fake (`FakeUpstreamHandler`) — aucun test ne
touche le vrai `api.cmicrolocks.fr`.

## Déploiement (CI/CD)

`.github/workflows/deploy.yml` déploie automatiquement sur le VPS partagé (`shared-vps`)
à chaque push sur `main`. Secret GitHub requis (`Settings > Secrets and variables >
Actions`) :

| Nom | Type | Valeur |
|---|---|---|
| `DEPLOY_SSH_KEY` | Secret | Même clé de déploiement que les autres repos Hizope/CMicrolocks |

Variables d'environnement posées côté VPS (`hizope-scaleway-deploy/shared-vps/.env`,
jamais commitées) : `CMICROLOCKS_PLATFORM_KEY` (même valeur que
`Platform__PilotageKey` côté `CMicrolocks-backend` — sinon 401 systématique),
`HIZOPE_PILOTAGE_STRIPE_SECRET_KEY` / `HIZOPE_PILOTAGE_STRIPE_TEST_SECRET_KEY` (CMicrolocks),
et pour LoveList :

| Variable VPS | Config API | Valeur |
|---|---|---|
| `LOVELIST_PLATFORM_KEY` | `LoveList__PlatformKey` | Même valeur que `Platform__PilotageKey` côté `LoveList-backend` |
| `HIZOPE_PILOTAGE_LOVELIST_STRIPE_SECRET_KEY` | `LoveList__Stripe__SecretKey` | Clé **restreinte** live du compte Stripe LoveList |
| `HIZOPE_PILOTAGE_LOVELIST_STRIPE_TEST_SECRET_KEY` | `LoveList__Stripe__TestSecretKey` | Clé restreinte test |

Clés Stripe restreintes (Dashboard Stripe > Développeurs > Clés API > Créer une clé
restreinte), en **lecture seule** : `Charges`, `Balance`, `Payouts` en *Read*, rien
d'autre. Ne pas réutiliser `LOVELIST_STRIPE_SECRET_KEY` (clé complète de LoveList-backend,
qui crée de vrais abonnements). Clé absente → les routes répondent 503 « Clé Stripe non
configurée », le reste du tableau de bord continue de marcher.
