# Tetragon fetch 사이드카 — Playwright 공식 이미지(브라우저·의존성 포함)
FROM mcr.microsoft.com/playwright:v1.48.0-jammy
WORKDIR /app
COPY package.json ./
RUN npm install --omit=dev
# rebrowser-playwright는 자체 Playwright 버전의 브라우저 빌드를 요구한다.
RUN npx playwright install chromium
COPY src ./src
ENV PORT=8080
EXPOSE 8080
CMD ["node", "src/server.js"]
