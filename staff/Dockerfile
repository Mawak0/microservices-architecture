FROM node:22-bookworm-slim AS build
WORKDIR /app
COPY staff/package*.json ./
RUN npm ci
COPY staff/ ./
RUN npm run build && npm prune --omit=dev

FROM node:22-bookworm-slim
WORKDIR /app
ENV NODE_ENV=production STAFF_BIND=0.0.0.0
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/dist ./dist
COPY --from=build /app/server ./server
USER node
EXPOSE 19184
CMD ["node", "server/start.mjs"]
