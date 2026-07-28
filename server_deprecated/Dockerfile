FROM oven/bun:1

WORKDIR /app

COPY packages/shared/package.json packages/shared/package.json
COPY packages/shared/src packages/shared/src
COPY server/package.json server/package.json
COPY server/src server/src

WORKDIR /app/server
RUN bun install --production

ENV PORT=8787
ENV DB_PATH=/data/ai-life.db

EXPOSE 8787

CMD ["bun", "run", "dev"]
