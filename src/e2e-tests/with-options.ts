import { test as base } from '@playwright/test';

export type TestOptions = {
    authority: string,
    username: string,
    password: string
};

export const test = base.extend<TestOptions>({
    authority: ['', {option: true}],
    username: ['', {option: true}],
    password: ['', {option: true}]
});
