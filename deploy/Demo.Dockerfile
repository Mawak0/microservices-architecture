FROM node:22.14.0-bookworm-slim
WORKDIR /app
COPY demo/package*.json demo/
WORKDIR /app/demo
RUN npm ci --omit=dev
WORKDIR /app
COPY demo/ demo/
RUN mkdir .demo-data && chown node:node .demo-data
USER node
ENV DEMO_EXTERNAL_SERVICES=true DEMO_BIND=0.0.0.0
HEALTHCHECK --interval=5s --timeout=3s --retries=24 CMD node -e "fetch('http://localhost:19180/health').then(r=>{if(!r.ok)process.exit(1)}).catch(()=>process.exit(1))"
CMD ["node", "demo/stand.mjs"]
