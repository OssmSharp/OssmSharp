import {defineConfig} from '@playwright/test';
export default defineConfig({testDir:'./tests',use:{headless:true,launchOptions:{executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE,args:['--no-sandbox']}}});
