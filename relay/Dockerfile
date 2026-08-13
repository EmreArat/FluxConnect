# FluxConnect Relay — üretim imajı
#
# Güvenlik notu: Bu servis internete AÇIK ve kimlik doğrulaması YOK (adresi bilen
# herkes bağlanabilir — tasarım kararı). Bu yüzden container mümkün olduğunca
# yetkisiz: non-root kullanıcı, salt-okunur dosya sistemi (compose'da), kabuk yok.
# Ele geçirilse bile host'a ya da diğer container'lara (DB'ler) sıçrayamaz.

# ---- 1) Derleme katmanı ----
FROM node:22-slim AS derleme
WORKDIR /app

# Önce yalnızca manifest: bağımlılık katmanı kod değişince yeniden kurulmasın.
COPY package*.json ./
RUN npm ci

COPY tsconfig.json ./
COPY src ./src
RUN npm run build

# Üretim bağımlılıkları ayrı bir ağaçta (devDependencies imaja girmesin)
RUN npm ci --omit=dev

# ---- 2) Çalışma katmanı ----
FROM node:22-slim
WORKDIR /app

ENV NODE_ENV=production

# node imajı zaten uid/gid 1000 ile "node" kullanıcısı taşıyor — root çalıştırmıyoruz.
COPY --from=derleme --chown=node:node /app/node_modules ./node_modules
COPY --from=derleme --chown=node:node /app/dist ./dist
COPY --from=derleme --chown=node:node /app/package.json ./package.json

USER node

EXPOSE 8765

# Sağlık: /health 200 dönmezse container "unhealthy" olur.
# wget/curl yok (slim imaj) — node'un kendisiyle kontrol ediyoruz.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD node -e "require('http').get('http://127.0.0.1:'+(process.env.PORT||8765)+'/health',r=>process.exit(r.statusCode===200?0:1)).on('error',()=>process.exit(1))"

CMD ["node", "dist/server.js"]
