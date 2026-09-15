import { api } from './client';

/* =====================================================================
   /api/me — who the backend decided the signed-in person is.

   Two different questions, answered by two different systems: the Microsoft
   token says which account signed in, this says what that account is allowed
   to be inside MGT Document OCR (which company, which role). Never trust the
   token alone for the second one.
   ===================================================================== */

export interface MeCompany {
  companyId: number;
  companyCode: string;   // the SAP company code
  companyName: string;
  isPrimary: boolean;
}

export interface Me {
  userId: string;
  username: string;
  email: string;
  fullName: string;
  role: string;          // Admin | Leader | Manager | user
  department: string;
  position: string;
  salesOrganization: string;
  division: string;
  companies: MeCompany[];
  primaryCompany: Omit<MeCompany, 'isPrimary'> | null;
}

export const getMe = () => api.get<Me>('/api/me');
